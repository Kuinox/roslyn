// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.DesignerAttributes;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Versions;
using Microsoft.VisualStudio.Designer.Interfaces;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.Shell.Interop;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.DesignerAttribute
{
    internal partial class DesignerAttributeIncrementalAnalyzer : ForegroundThreadAffinitizedObject, IIncrementalAnalyzer
    {
        private readonly IForegroundNotificationService _notificationService;

        private readonly IServiceProvider _serviceProvider;
        private readonly DesignerAttributeState _state;
        private readonly IAsynchronousOperationListener _listener;

        /// <summary>
        /// cache designer from UI thread
        /// 
        /// access this field through <see cref="GetDesignerFromForegroundThread"/>
        /// </summary>
        private IVSMDDesignerService _dotNotAccessDirectlyDesigner;

        public DesignerAttributeIncrementalAnalyzer(
            IServiceProvider serviceProvider,
            IForegroundNotificationService notificationService,
            IEnumerable<Lazy<IAsynchronousOperationListener, FeatureMetadata>> asyncListeners)
        {
            _serviceProvider = serviceProvider;
            Contract.ThrowIfNull(_serviceProvider);

            _notificationService = notificationService;

            _listener = new AggregateAsynchronousOperationListener(asyncListeners, FeatureAttribute.DesignerAttribute);
            _state = new DesignerAttributeState();
        }

        public Task DocumentResetAsync(Document document, CancellationToken cancellationToken)
        {
            _state.Remove(document.Id);
            return _state.PersistAsync(document, new Data(VersionStamp.Default, VersionStamp.Default, designerAttributeArgument: null), cancellationToken);
        }

        public bool NeedsReanalysisOnOptionChanged(object sender, OptionChangedEventArgs e)
        {
            return false;
        }

        public async Task AnalyzeDocumentAsync(Document document, SyntaxNode bodyOpt, InvocationReasons reasons, CancellationToken cancellationToken)
        {
            Contract.ThrowIfFalse(document.IsFromPrimaryBranch());

            cancellationToken.ThrowIfCancellationRequested();

            if (!document.Project.Solution.Workspace.Options.GetOption(InternalFeatureOnOffOptions.DesignerAttributes))
            {
                return;
            }

            // use tree version so that things like compiler option changes are considered
            var textVersion = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            var projectVersion = await document.Project.GetDependentVersionAsync(cancellationToken).ConfigureAwait(false);
            var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

            var existingData = await _state.TryGetExistingDataAsync(document, cancellationToken).ConfigureAwait(false);
            if (existingData != null)
            {
                // check whether we can use the data as it is (can happen when re-using persisted data from previous VS session)
                if (CheckVersions(document, textVersion, projectVersion, semanticVersion, existingData))
                {
                    RegisterDesignerAttribute(document, existingData.DesignerAttributeArgument);
                    return;
                }
            }

            var result = await ScanDesignerAttributesOnRemoteHostIfPossibleAsync(document, cancellationToken).ConfigureAwait(false);
            if (result.NotApplicable)
            {
                _state.Remove(document.Id);
                return;
            }

            // we checked all types in the document, but couldn't find designer attribute, but we can't say this document doesn't have designer attribute
            // if the document also contains some errors.
            var designerAttributeArgumentOpt = result.ContainsErrors ? new Optional<string>() : new Optional<string>(result.DesignerAttributeArgument);
            await RegisterDesignerAttributeAndSaveStateAsync(document, textVersion, semanticVersion, designerAttributeArgumentOpt, cancellationToken).ConfigureAwait(false);
        }

        private async Task<DesignerAttributeResult> ScanDesignerAttributesOnRemoteHostIfPossibleAsync(Document document, CancellationToken cancellationToken)
        {
            var service = document.GetLanguageService<IDesignerAttributeService>();
            if (service == null)
            {
                return new DesignerAttributeResult(designerAttributeArgument: null, containsErrors: true, notApplicable: true);
            }

            return await service.ScanDesignerAttributesAsync(document, cancellationToken).ConfigureAwait(false);
        }

        private bool CheckVersions(
            Document document, VersionStamp textVersion, VersionStamp dependentProjectVersion, VersionStamp dependentSemanticVersion, Data existingData)
        {
            // first check full version to see whether we can reuse data in same session, if we can't, check timestamp only version to see whether
            // we can use it cross-session.
            return document.CanReusePersistedTextVersion(textVersion, existingData.TextVersion) &&
                   document.Project.CanReusePersistedDependentSemanticVersion(dependentProjectVersion, dependentSemanticVersion, existingData.SemanticVersion);
        }

        private async Task RegisterDesignerAttributeAndSaveStateAsync(
            Document document, VersionStamp textVersion, VersionStamp semanticVersion, Optional<string> designerAttributeArgumentOpt, CancellationToken cancellationToken)
        {
            if (!designerAttributeArgumentOpt.HasValue)
            {
                // no value means it couldn't determine whether this document has designer attribute or not.
                // one of such case is when base type is error type.
                return;
            }

            var data = new Data(textVersion, semanticVersion, designerAttributeArgumentOpt.Value);
            await _state.PersistAsync(document, data, cancellationToken).ConfigureAwait(false);

            RegisterDesignerAttribute(document, designerAttributeArgumentOpt.Value);
        }

        private void RegisterDesignerAttribute(Document document, string designerAttributeArgument)
        {
            var workspace = document.Project.Solution.Workspace as VisualStudioWorkspaceImpl;
            if (workspace == null)
            {
                return;
            }

            var documentId = document.Id;
            _notificationService.RegisterNotification(() =>
            {
                var vsDocument = workspace.GetHostDocument(documentId);
                if (vsDocument == null)
                {
                    return;
                }

                uint itemId = vsDocument.GetItemId();
                if (itemId == (uint)VSConstants.VSITEMID.Nil)
                {
                    // it is no longer part of the solution
                    return;
                }

                if (ErrorHandler.Succeeded(vsDocument.Project.Hierarchy.GetProperty(itemId, (int)__VSHPROPID.VSHPROPID_ItemSubType, out var currentValue)))
                {
                    var currentStringValue = string.IsNullOrEmpty(currentValue as string) ? null : (string)currentValue;
                    if (string.Equals(currentStringValue, designerAttributeArgument, StringComparison.OrdinalIgnoreCase))
                    {
                        // PERF: Avoid sending the message if the project system already has the current value.
                        return;
                    }
                }

                try
                {
                    var designer = GetDesignerFromForegroundThread();
                    if (designer != null)
                    {
                        designer.RegisterDesignViewAttribute(vsDocument.Project.Hierarchy, (int)itemId, dwClass: 0, pwszAttributeValue: designerAttributeArgument);
                    }
                }
                catch
                {
                    // DevDiv # 933717
                    // turns out RegisterDesignViewAttribute can throw in certain cases such as a file failed to be checked out by source control
                    // or IVSHierarchy failed to set a property for this project
                    //
                    // just swallow it. don't crash VS.
                }
            }, _listener.BeginAsyncOperation("RegisterDesignerAttribute"));
        }

        private IVSMDDesignerService GetDesignerFromForegroundThread()
        {
            if (_dotNotAccessDirectlyDesigner != null)
            {
                return _dotNotAccessDirectlyDesigner;
            }

            AssertIsForeground();
            _dotNotAccessDirectlyDesigner = _serviceProvider.GetService(typeof(SVSMDDesignerService)) as IVSMDDesignerService;

            return _dotNotAccessDirectlyDesigner;
        }

        public void RemoveDocument(DocumentId documentId)
        {
            _state.Remove(documentId);
        }

        private class Data
        {
            public readonly VersionStamp TextVersion;
            public readonly VersionStamp SemanticVersion;
            public readonly string DesignerAttributeArgument;

            public Data(VersionStamp textVersion, VersionStamp semanticVersion, string designerAttributeArgument)
            {
                this.TextVersion = textVersion;
                this.SemanticVersion = semanticVersion;
                this.DesignerAttributeArgument = designerAttributeArgument;
            }
        }

        #region unused
        public Task NewSolutionSnapshotAsync(Solution solution, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public Task DocumentOpenAsync(Document document, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public Task DocumentCloseAsync(Document document, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public Task AnalyzeSyntaxAsync(Document document, InvocationReasons reasons, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public Task AnalyzeProjectAsync(Project project, bool semanticsChanged, InvocationReasons reasons, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public void RemoveProject(ProjectId projectId)
        {
        }
        #endregion
    }
}
