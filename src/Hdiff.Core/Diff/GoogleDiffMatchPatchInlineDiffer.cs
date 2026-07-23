using DiffMatchPatch;

namespace Hdiff.Core.Diff;

/// <summary>
/// Refines an already-paired modified paragraph with Google's Diff Match Patch.
/// It deliberately does not decide paragraph alignment; DiffPlex remains the
/// single source of truth for inserted, deleted, and modified document rows.
/// </summary>
internal static class GoogleDiffMatchPatchInlineDiffer
{
    private const float TimeoutSeconds = 1.0f;

    public static bool TryCreateFragments(
        string oldText,
        string newText,
        out IReadOnlyList<InlineDiffFragment> oldFragments,
        out IReadOnlyList<InlineDiffFragment> newFragments)
    {
        try
        {
            // False avoids a second line-level strategy. A DiffRow is already one
            // paired paragraph/line, so we want only its internal character diff.
            var diffs = DiffMatchPatch.Diff.Compute(oldText, newText, TimeoutSeconds, false);
            DiffList.CleanupSemantic(diffs);

            var oldResult = new List<InlineDiffFragment>();
            var newResult = new List<InlineDiffFragment>();
            foreach (var diff in diffs)
            {
                if (string.IsNullOrEmpty(diff.Text)) continue;
                switch (diff.Operation)
                {
                    case Operation.Equal:
                        Append(oldResult, InlineDiffFragmentKind.Unchanged, diff.Text);
                        Append(newResult, InlineDiffFragmentKind.Unchanged, diff.Text);
                        break;
                    case Operation.Delete:
                        Append(oldResult, InlineDiffFragmentKind.Removed, diff.Text);
                        break;
                    case Operation.Insert:
                        Append(newResult, InlineDiffFragmentKind.Added, diff.Text);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported Google diff operation: {diff.Operation}");
                }
            }

            // A defensive reconstruction check guarantees that a third-party
            // cleanup can never make the rendered source text inaccurate.
            if (!string.Equals(Join(oldResult), oldText, StringComparison.Ordinal) ||
                !string.Equals(Join(newResult), newText, StringComparison.Ordinal))
            {
                oldFragments = Array.Empty<InlineDiffFragment>();
                newFragments = Array.Empty<InlineDiffFragment>();
                return false;
            }

            oldFragments = oldResult;
            newFragments = newResult;
            return true;
        }
        catch
        {
            oldFragments = Array.Empty<InlineDiffFragment>();
            newFragments = Array.Empty<InlineDiffFragment>();
            return false;
        }
    }

    private static void Append(List<InlineDiffFragment> fragments, InlineDiffFragmentKind kind, string text)
    {
        if (fragments.LastOrDefault() is { } previous && previous.Kind == kind)
        {
            fragments[^1] = previous with { Text = previous.Text + text };
        }
        else
        {
            fragments.Add(new InlineDiffFragment(kind, text));
        }
    }

    private static string Join(IEnumerable<InlineDiffFragment> fragments) => string.Concat(fragments.Select(fragment => fragment.Text));
}
