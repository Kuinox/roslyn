﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal abstract class WithTypeParametersBinder : Binder
    {
        internal WithTypeParametersBinder(Binder next)
            : base(next)
        {
        }

        // TODO: Change this to a data structure that won't allocate enumerators
        protected abstract MultiDictionary<string, TypeParameterSymbol> TypeParameterMap { get; }

        // This is only overridden by WithMethodTypeParametersBinder.
        protected virtual LookupOptions LookupMask
        {
            get
            {
                return LookupOptions.NamespaceAliasesOnly | LookupOptions.MustBeInvocableIfMember;
            }
        }

        protected bool CanConsiderTypeParameters(LookupOptions options)
        {
            return (options & (LookupMask | LookupOptions.MustBeInstance | LookupOptions.LabelsOnly)) == 0;
        }

        internal override void LookupSymbolsInSingleBinder(
            LookupResult result, string name, int arity, ConsList<TypeSymbol> basesBeingResolved, LookupOptions options, Binder originalBinder, bool diagnose, ref HashSet<DiagnosticInfo> useSiteDiagnostics)
        {
            Debug.Assert(result.IsClear);

            if ((options & LookupMask) != 0)
            {
                return;
            }

            foreach (var typeParameter in TypeParameterMap[name])
            {
                result.MergeEqual(originalBinder.CheckViability(typeParameter, arity, options, null, diagnose, ref useSiteDiagnostics));
            }
        }
    }
}
