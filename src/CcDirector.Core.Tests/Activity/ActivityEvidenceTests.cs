using CcDirector.Core.Activity;
using Xunit;

namespace CcDirector.Core.Tests.Activity;

/// <summary>
/// The bounded terminal evidence helpers: the hash treats trailing padding as presentation, and the diff
/// quotes only a bounded head while never claiming to be complete when it is not.
/// </summary>
public sealed class ActivityEvidenceTests
{
    [Fact]
    public void The_hash_ignores_trailing_whitespace_but_not_content()
    {
        Assert.Equal(ActivityEvidence.BodyHash("hello\nworld"), ActivityEvidence.BodyHash("hello   \nworld  "));
        Assert.NotEqual(ActivityEvidence.BodyHash("hello\nworld"), ActivityEvidence.BodyHash("hello\nworld!"));
    }

    [Fact]
    public void An_empty_body_hashes_stably()
    {
        Assert.Equal(ActivityEvidence.BodyHash(""), ActivityEvidence.BodyHash(""));
        Assert.Equal(32, ActivityEvidence.BodyHash("").Length);
    }

    [Fact]
    public void The_diff_quotes_only_the_changed_rows()
    {
        var before = "one\ntwo\nthree";
        var after = "one\nTWO\nthree\nfour";

        var diff = ActivityEvidence.BoundedRowDiff(before, after);

        Assert.Equal("row 1: TWO\nrow 3: four", diff);
    }

    [Fact]
    public void A_removed_row_is_named_not_silently_absent()
    {
        var diff = ActivityEvidence.BoundedRowDiff("one\ntwo", "one");
        Assert.Equal("row 1: <removed>", diff);
    }

    [Fact]
    public void Identical_bodies_diff_to_nothing()
    {
        Assert.Equal("", ActivityEvidence.BoundedRowDiff("same\nbody", "same\nbody"));
        // Trailing padding is presentation, not change.
        Assert.Equal("", ActivityEvidence.BoundedRowDiff("same\nbody", "same   \nbody  "));
    }

    [Fact]
    public void A_diff_larger_than_the_bound_says_how_much_it_left_out()
    {
        var before = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"row-{i}"));
        var after = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"changed-{i}"));

        var diff = ActivityEvidence.BoundedRowDiff(before, after);

        var quoted = diff.Split('\n');
        Assert.Equal(ActivityEvidence.MaxDiffRows + 1, quoted.Length); // the quoted head + the tail note
        Assert.Contains("more changed row(s) not quoted", quoted[^1]);
        Assert.Contains("12", quoted[^1]); // 20 changed, 8 quoted
    }

    [Fact]
    public void The_diff_never_exceeds_its_character_cap_by_more_than_the_tail_note()
    {
        var wide = new string('x', 900);
        var before = string.Join('\n', Enumerable.Range(0, 10).Select(_ => "short"));
        var after = string.Join('\n', Enumerable.Range(0, 10).Select(i => wide + i));

        var diff = ActivityEvidence.BoundedRowDiff(before, after);

        Assert.True(diff.Length <= ActivityEvidence.MaxDiffChars + 100,
            $"diff length {diff.Length} exceeds the cap plus the tail note");
    }
}
