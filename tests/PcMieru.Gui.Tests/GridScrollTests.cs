namespace PcMieru.Gui.Tests;

/// <summary>
/// A native list keeps the place it was scrolled to when its window grows: when every row fits at last, its first row is shown far below the
/// header (the list reports a negative top row) and nothing brings it back. A grid that asks for it (<see cref="GridView.ScrollsBackWhenWhole"/>:
/// the lists of the recording tab) goes back to its first row before it grows to hold all its rows, and one that is scrolled for a longer
/// list can be put back by hand (<see cref="GridView.ScrollToFirstRow"/>). The list is shown off screen: the native layout is only there for
/// a window that has been shown.
/// </summary>
public class GridScrollTests
{
    private sealed class Rig : IDisposable
    {
        private readonly Form _form = new() { StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), Size = new Size(700, 700), ShowInTaskbar = false };

        public Rig(int rows, int fitRows, bool scrollsBack)
        {
            Grid = new GridView { Cells = (row, column) => new GridCell("r" + row + "c" + column), ScrollsBackWhenWhole = scrollsBack };
            PageLayout.Prepare(Grid, "scrollProbe", ("a", 200, HorizontalAlignment.Left), ("b", 200, HorizontalAlignment.Left));
            Grid.Dock = DockStyle.None;
            Grid.Location = new Point(0, 0);
            Grid.Size = new Size(600, 200);
            _form.Controls.Add(Grid);
            _form.Show();
            Settle();
            Grid.VirtualListSize = rows;
            Settle();
            Grid.Height = Grid.HeightForRows(fitRows);
            Settle();
            RestTop = Grid.RowRect(0)?.Top ?? -1;
        }

        public GridView Grid { get; }

        /// <summary>Where the first row sits when the list is not scrolled.</summary>
        public int RestTop { get; }

        /// <summary>Waits until the list has taken what was done to it (see <see cref="Ui.Settle"/>): what it reports no longer changes.</summary>
        public void Settle() => Ui.Settle(() => Facts);

        public string Facts => $"top {Grid.TopRow}, size {Grid.VirtualListSize}, fit {Grid.RowsThatFit(Grid.Height)}, first row at {Grid.RowRect(0)?.Top} px (at rest {RestTop} px)";

        public bool FirstRowAtRest => Grid.TopRow == 0 && Grid.RowRect(0) is { } first && Math.Abs(first.Top - RestTop) <= 2;

        public void Dispose()
        {
            Grid.Dispose();
            _form.Dispose();
        }
    }

    [Fact]
    public void A_scrolled_list_is_put_back_at_its_first_row_by_hand()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(rows: 12, fitRows: 4, scrollsBack: false);
            Assert.True(rig.RestTop > 0, "no first row: " + rig.Facts);
            rig.Grid.SelectRow(11, scroll: true);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list did not scroll: " + rig.Facts);

            rig.Grid.ScrollToFirstRow();
            rig.Settle();
            Assert.True(rig.FirstRowAtRest, "the first row is not under the header: " + rig.Facts);
        });
    }

    [Fact]
    public void A_list_that_asks_for_it_goes_back_to_its_first_row_before_it_grows_to_hold_all_its_rows()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(rows: 12, fitRows: 4, scrollsBack: true);
            rig.Grid.SelectRow(11, scroll: true);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list did not scroll: " + rig.Facts);

            rig.Grid.SelectedIndices.Clear();
            rig.Grid.Height = rig.Grid.HeightForRows(12);
            rig.Settle();
            Assert.True(rig.FirstRowAtRest, "the first row is not under the header after the list grew: " + rig.Facts);
        });
    }

    [Fact]
    public void A_list_that_asks_for_it_keeps_its_place_when_it_still_has_more_rows_than_room()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(rows: 12, fitRows: 4, scrollsBack: true);
            rig.Grid.SelectRow(11, scroll: true);
            rig.Settle();
            var scrolled = rig.Grid.TopRow;
            Assert.True(scrolled > 0, "the list did not scroll: " + rig.Facts);

            // Room for six of the twelve rows: it is where the person scrolled it (as far as the longer window lets it be).
            rig.Grid.Height = rig.Grid.HeightForRows(6);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list went back to its first row with rows left out of sight: " + rig.Facts);
        });
    }

    [Fact]
    public void A_list_that_asks_for_it_goes_back_to_its_first_row_when_its_rows_are_made_fewer_than_the_room()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(rows: 5, fitRows: 4, scrollsBack: true);
            rig.Grid.SelectRow(4, scroll: true);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list did not scroll: " + rig.Facts);

            rig.Grid.SelectedIndices.Clear();
            rig.Grid.SetRowCount(4);
            rig.Settle();
            Assert.True(rig.FirstRowAtRest, "the first row is not under the header after the rows were made fewer: " + rig.Facts);

            // Still more rows than room: the place it was scrolled to is kept.
            rig.Grid.SetRowCount(9);
            rig.Settle();
            rig.Grid.SelectRow(8, scroll: true);
            rig.Settle();
            rig.Grid.SelectedIndices.Clear();
            var scrolled = rig.Grid.TopRow;
            Assert.True(scrolled > 0, "the list did not scroll: " + rig.Facts);
            rig.Grid.SetRowCount(8);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list went back to its first row with rows left out of sight: " + rig.Facts);
        });
    }

    [Fact]
    public void The_whole_rows_check_names_a_list_scrolled_although_all_its_rows_fit()
    {
        // The check below it starts from the top row on show and takes it for the first row, so a list scrolled with all its rows in the room would pass it.
        var problem = GridView.ScrolledWithRoomProblem(topRow: 2, rows: 3, fit: 4);
        Assert.NotNull(problem);
        Assert.Contains("scrolled to row 2", problem, StringComparison.Ordinal);
        Assert.Contains("all 3 rows fit", problem, StringComparison.Ordinal);

        Assert.NotNull(GridView.ScrolledWithRoomProblem(topRow: 1, rows: 4, fit: 4));
        Assert.Null(GridView.ScrolledWithRoomProblem(topRow: 0, rows: 3, fit: 4));
        Assert.Null(GridView.ScrolledWithRoomProblem(topRow: 8, rows: 12, fit: 4));
        Assert.Null(GridView.ScrolledWithRoomProblem(topRow: 1, rows: 5, fit: 4));
    }

    [Fact]
    public void The_whole_rows_check_reports_a_list_that_kept_its_place_when_its_window_grew_to_hold_every_row()
    {
        Ui.Run(_ =>
        {
            // Twelve rows with room for four, scrolled to the last; the window then grows to hold all twelve. The native list does not put itself back (its
            // first row sits far under the header), and the list here does not ask to be put back: the check is what must say so.
            using var rig = new Rig(rows: 12, fitRows: 4, scrollsBack: false);
            rig.Grid.SelectRow(11, scroll: true);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list did not scroll: " + rig.Facts);

            rig.Grid.SelectedIndices.Clear();
            rig.Grid.Height = rig.Grid.HeightForRows(12);
            rig.Settle();
            Assert.True(rig.Grid.RowsThatFit(rig.Grid.Height) >= 12, "the test needs room for all twelve rows: " + rig.Facts);

            var problem = rig.Grid.WholeRowsProblem(out var facts);
            Assert.NotNull(problem);
            Assert.Contains("first row starts at", problem, StringComparison.Ordinal);
            Assert.Contains("12 of 12 rows", facts, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_whole_rows_check_is_quiet_for_a_list_that_asked_to_go_back_and_did()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(rows: 4, fitRows: 3, scrollsBack: true);
            rig.Grid.SelectRow(3, scroll: true);
            rig.Settle();
            Assert.True(rig.Grid.TopRow > 0, "the list did not scroll: " + rig.Facts);

            rig.Grid.SelectedIndices.Clear();
            rig.Grid.SetRowCount(3);
            rig.Settle();
            var problem = rig.Grid.WholeRowsProblem(out var facts);
            Assert.True(problem is null, problem + " " + facts);
        });
    }

    [Fact]
    public void A_list_that_is_not_scrolled_or_has_no_rows_is_left_alone()
    {
        Ui.Run(_ =>
        {
            using var rig = new Rig(rows: 3, fitRows: 4, scrollsBack: true);
            rig.Grid.ScrollToFirstRow();
            Assert.True(rig.FirstRowAtRest, rig.Facts);
            rig.Grid.Height = rig.Grid.HeightForRows(6);
            rig.Settle();
            Assert.True(rig.FirstRowAtRest, rig.Facts);

            rig.Grid.VirtualListSize = 0;
            rig.Grid.ScrollToFirstRow();
            Assert.Equal(0, rig.Grid.TopRow);

            using var bare = new GridView();
            bare.ScrollToFirstRow();
            Assert.Equal(0, bare.TopRow);
        });
    }
}
