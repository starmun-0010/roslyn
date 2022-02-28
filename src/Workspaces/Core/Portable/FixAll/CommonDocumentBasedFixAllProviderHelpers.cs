﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FixAll
{
    /// <summary>
    /// Helper methods for DocumentBasedFixAllProvider common to code fixes and refactorings.
    /// </summary>
    internal static class CommonDocumentBasedFixAllProviderHelpers
    {
        /// <summary>
        /// Take all the fixed documents and format/simplify/clean them up (if the language supports that), and take the
        /// resultant text and apply it to the solution.  If the language doesn't support cleanup, then just take the
        /// given text and apply that instead.
        /// </summary>
        internal static async Task<Solution> CleanupAndApplyChangesAsync(
            IProgressTracker progressTracker,
            Solution currentSolution,
            Dictionary<DocumentId, (SyntaxNode? node, SourceText? text)> docIdToNewRootOrText,
            CancellationToken cancellationToken)
        {
            using var _1 = progressTracker.ItemCompletedScope();

            if (docIdToNewRootOrText.Count > 0)
            {
                // Next, go and insert those all into the solution so all the docs in this particular project point at
                // the new trees (or text).  At this point though, the trees have not been cleaned up.  We don't cleanup
                // the documents as they are created, or one at a time as we add them, as that would cause us to run
                // cleanup on N different solution forks (which would be very expensive).  Instead, by adding all the
                // changed documents to one solution, and hten cleaning *those* we only perform cleanup semantics on one
                // forked solution.
                foreach (var (docId, (newRoot, newText)) in docIdToNewRootOrText)
                {
                    currentSolution = newRoot != null
                        ? currentSolution.WithDocumentSyntaxRoot(docId, newRoot)
                        : currentSolution.WithDocumentText(docId, newText!);
                }

                // Next, go and cleanup any trees we inserted. Once we clean the document, we get the text of it and
                // insert that back into the final solution.  This way we can release both the original fixed tree, and
                // the cleaned tree (both of which can be much more expensive than just text).
                //
                // Do this in parallel across all the documents that were fixed.
                using var _2 = ArrayBuilder<Task<(DocumentId docId, SourceText sourceText)>>.GetInstance(out var tasks);

                foreach (var (docId, (newRoot, _)) in docIdToNewRootOrText)
                {
                    if (newRoot != null)
                    {
                        var dirtyDocument = currentSolution.GetRequiredDocument(docId);
                        tasks.Add(Task.Run(async () =>
                        {
                            var cleanedDocument = await PostProcessCodeAction.Instance.PostProcessChangesAsync(dirtyDocument, cancellationToken).ConfigureAwait(false);
                            var cleanedText = await cleanedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                            return (dirtyDocument.Id, cleanedText);
                        }, cancellationToken));
                    }
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Finally, apply the cleaned documents to the solution.
                foreach (var task in tasks)
                {
                    var (docId, cleanedText) = await task.ConfigureAwait(false);
                    currentSolution = currentSolution.WithDocumentText(docId, cleanedText);
                }
            }

            return currentSolution;
        }

        /// <summary>
        /// Dummy class just to get access to <see cref="CodeAction.PostProcessChangesAsync(Document, CancellationToken)"/>
        /// </summary>
        private class PostProcessCodeAction : CodeAction
        {
            public static readonly PostProcessCodeAction Instance = new();

            public override string Title => "";

            public new Task<Document> PostProcessChangesAsync(Document document, CancellationToken cancellationToken)
                => base.PostProcessChangesAsync(document, cancellationToken);
        }
    }
}
