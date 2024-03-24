﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Squiggles;

internal sealed class TestDiagnosticTagProducer<TProvider, TTag>
    where TProvider : AbstractDiagnosticsTaggerProvider<TTag>
    where TTag : class, ITag
{
    internal static Task<(ImmutableArray<DiagnosticData>, ImmutableArray<ITagSpan<TTag>>)> GetDiagnosticsAndErrorSpans(
        EditorTestWorkspace workspace,
        IReadOnlyDictionary<string, ImmutableArray<DiagnosticAnalyzer>>? analyzerMap = null)
    {
        return SquiggleUtilities.GetDiagnosticsAndErrorSpansAsync<TProvider, TTag>(workspace, analyzerMap);
    }

    internal static DiagnosticData CreateDiagnosticData(EditorTestHostDocument document, TextSpan span)
    {
        Contract.ThrowIfNull(document.FilePath);

        var sourceText = document.GetTextBuffer().CurrentSnapshot.AsText();
        var linePosSpan = sourceText.Lines.GetLinePositionSpan(span);
        return new DiagnosticData(
            id: "test",
            category: "test",
            message: "test",
            severity: DiagnosticSeverity.Error,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            warningLevel: 0,
            projectId: document.Project.Id,
            customTags: ImmutableArray<string>.Empty,
            properties: ImmutableDictionary<string, string?>.Empty,
            location: new DiagnosticDataLocation(new FileLinePositionSpan(document.FilePath, linePosSpan), document.Id),
            language: document.Project.Language);
    }

    private class TestDiagnosticUpdateSource
    {
        public void RaiseDiagnosticsUpdated(ImmutableArray<DiagnosticsUpdatedArgs> args)
        {
            DiagnosticsUpdated?.Invoke(this, args);
        }

        public event EventHandler<ImmutableArray<DiagnosticsUpdatedArgs>>? DiagnosticsUpdated;
    }
}
