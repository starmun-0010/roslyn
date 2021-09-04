﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols
{
    internal static partial class DeclarationFinder
    {
        private static Task AddCompilationDeclarationsWithNormalQueryAsync(
            Project project, SearchQuery query, SymbolFilter filter,
            ArrayBuilder<ISymbol> list, CancellationToken cancellationToken)
        {
            Contract.ThrowIfTrue(query.Kind == SearchKind.Custom, "Custom queries are not supported in this API");
            return AddCompilationDeclarationsWithNormalQueryAsync(
                project, query, filter, list,
                startingCompilation: null,
                startingAssembly: null,
                cancellationToken: cancellationToken);
        }

        private static async Task AddCompilationDeclarationsWithNormalQueryAsync(
            Project project,
            SearchQuery query,
            SymbolFilter filter,
            ArrayBuilder<ISymbol> list,
            Compilation startingCompilation,
            IAssemblySymbol startingAssembly,
            CancellationToken cancellationToken)
        {
            if (!project.SupportsCompilation)
                return;

            Contract.ThrowIfTrue(query.Kind == SearchKind.Custom, "Custom queries are not supported in this API");

            using (Logger.LogBlock(FunctionId.SymbolFinder_Project_AddDeclarationsAsync, cancellationToken))
            {
                var syntaxFacts = project.LanguageServices.GetService<ISyntaxFactsService>();

                // If this is an exact query, we can speed things up by just calling into the
                // compilation entrypoints that take a string directly.
                //
                // the search is 'exact' if it's either an exact-case-sensitive search,
                // or it's an exact-case-insensitive search and we're in a case-insensitive
                // language.
                var isExactNameSearch = query.Kind == SearchKind.Exact ||
                    (query.Kind == SearchKind.ExactIgnoreCase && !syntaxFacts.IsCaseSensitive);

                // Do a quick syntactic check first using our cheaply built indices.  That will help us avoid creating
                // a compilation here if it's not necessary.  In the case of an exact name search we can call a special 
                // overload that quickly uses the direct bloom-filter identifier maps in the index.  If it's nto an 
                // exact name search, then we will run the query's predicate over every DeclaredSymbolInfo stored in
                // the doc.
                var containsSymbol = isExactNameSearch
                    ? await ContainsSymbolsWithNameAsync(project, query.Name, cancellationToken).ConfigureAwait(false)
                    : await ContainsSymbolsWithNameAsync(project, query.GetPredicate(), filter, cancellationToken).ConfigureAwait(false);

                if (!containsSymbol)
                    return;

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

                var symbols = isExactNameSearch
                    ? compilation.GetSymbolsWithName(query.Name, filter, cancellationToken)
                    : compilation.GetSymbolsWithName(query.GetPredicate(), filter, cancellationToken);

                var symbolsWithName = symbols.ToImmutableArray();

                if (startingCompilation != null && startingAssembly != null && !Equals(compilation.Assembly, startingAssembly))
                {
                    // Return symbols from skeleton assembly in this case so that symbols have 
                    // the same language as startingCompilation.
                    symbolsWithName = symbolsWithName.Select(s => s.GetSymbolKey(cancellationToken).Resolve(startingCompilation, cancellationToken: cancellationToken).Symbol)
                                                     .WhereNotNull()
                                                     .ToImmutableArray();
                }

                list.AddRange(FilterByCriteria(symbolsWithName, filter));
            }
        }

        private static Task<bool> ContainsSymbolsWithNameAsync(
            Project project, string name, CancellationToken cancellationToken)
        {
            return ContainsSymbolsAsync(
                project,
                (index, cancellationToken) => index.ProbablyContainsIdentifier(name) || index.ProbablyContainsEscapedIdentifier(name),
                cancellationToken);
        }

        private static Task<bool> ContainsSymbolsWithNameAsync(
            Project project, Func<string, bool> predicate, SymbolFilter filter, CancellationToken cancellationToken)
        {
            return ContainsSymbolsAsync(
                project,
                (index, cancellationToken) =>
                {
                    foreach (var info in index.DeclaredSymbolInfos)
                    {
                        if (FilterMatches(info, filter) && predicate(info.Name))
                            return true;
                    }

                    return false;
                },
                cancellationToken);

            static bool FilterMatches(DeclaredSymbolInfo info, SymbolFilter filter)
            {
                switch (info.Kind)
                {
                    case DeclaredSymbolInfoKind.Namespace:
                        return (filter & SymbolFilter.Namespace) != 0;
                    case DeclaredSymbolInfoKind.Class:
                    case DeclaredSymbolInfoKind.Delegate:
                    case DeclaredSymbolInfoKind.Enum:
                    case DeclaredSymbolInfoKind.Interface:
                    case DeclaredSymbolInfoKind.Module:
                    case DeclaredSymbolInfoKind.Record:
                    case DeclaredSymbolInfoKind.RecordStruct:
                    case DeclaredSymbolInfoKind.Struct:
                        return (filter & SymbolFilter.Type) != 0;
                    case DeclaredSymbolInfoKind.Constant:
                    case DeclaredSymbolInfoKind.Constructor:
                    case DeclaredSymbolInfoKind.EnumMember:
                    case DeclaredSymbolInfoKind.Event:
                    case DeclaredSymbolInfoKind.ExtensionMethod:
                    case DeclaredSymbolInfoKind.Field:
                    case DeclaredSymbolInfoKind.Indexer:
                    case DeclaredSymbolInfoKind.Method:
                    case DeclaredSymbolInfoKind.Property:
                        return (filter & SymbolFilter.Member) != 0;
                    default:
                        throw ExceptionUtilities.UnexpectedValue(info.Kind);
                }
            }
        }

        private static async Task<bool> ContainsSymbolsAsync(
            Project project, Func<SyntaxTreeIndex, CancellationToken, bool> predicate, CancellationToken cancellationToken)
        {
            var tasks = project.Documents.Select(async d =>
            {
                var index = await SyntaxTreeIndex.GetIndexAsync(d, cancellationToken).ConfigureAwait(false);
                return index != null && predicate(index, cancellationToken);
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.Any(b => b);
        }

        private static async Task AddMetadataDeclarationsWithNormalQueryAsync(
            Project project, IAssemblySymbol assembly, PortableExecutableReference referenceOpt,
            SearchQuery query, SymbolFilter filter, ArrayBuilder<ISymbol> list,
            CancellationToken cancellationToken)
        {
            // All entrypoints to this function are Find functions that are only searching
            // for specific strings (i.e. they never do a custom search).
            Contract.ThrowIfTrue(query.Kind == SearchKind.Custom, "Custom queries are not supported in this API");

            using (Logger.LogBlock(FunctionId.SymbolFinder_Assembly_AddDeclarationsAsync, cancellationToken))
            {
                if (referenceOpt != null)
                {
                    var info = await SymbolTreeInfo.GetInfoForMetadataReferenceAsync(
                        project.Solution, referenceOpt, loadOnly: false, cancellationToken: cancellationToken).ConfigureAwait(false);

                    var symbols = await info.FindAsync(
                            query, assembly, filter, cancellationToken).ConfigureAwait(false);
                    list.AddRange(symbols);
                }
            }
        }

        internal static ImmutableArray<ISymbol> FilterByCriteria(ImmutableArray<ISymbol> symbols, SymbolFilter criteria)
            => symbols.WhereAsArray(s => MeetCriteria(s, criteria));

        private static bool MeetCriteria(ISymbol symbol, SymbolFilter filter)
        {
            if (!symbol.IsImplicitlyDeclared && !symbol.IsAccessor())
            {
                if (IsOn(filter, SymbolFilter.Namespace) && symbol.Kind == SymbolKind.Namespace)
                {
                    return true;
                }

                if (IsOn(filter, SymbolFilter.Type) && symbol is ITypeSymbol)
                {
                    return true;
                }

                if (IsOn(filter, SymbolFilter.Member) && IsNonTypeMember(symbol))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsNonTypeMember(ISymbol symbol)
        {
            return symbol.Kind == SymbolKind.Method ||
                   symbol.Kind == SymbolKind.Property ||
                   symbol.Kind == SymbolKind.Event ||
                   symbol.Kind == SymbolKind.Field;
        }

        private static bool IsOn(SymbolFilter filter, SymbolFilter flag)
            => (filter & flag) == flag;
    }
}
