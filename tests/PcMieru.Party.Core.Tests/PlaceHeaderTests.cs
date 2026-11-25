using PcMieru.Party.Adventure;
using PcMieru.Party.Game;

namespace PcMieru.Party.Core.Tests;

/// <summary>章題の横に出す、いま居る所の名前。</summary>
public class PlaceHeaderTests
{
    [Fact]
    public void English_place_names_start_with_a_capital_in_the_header_and_japanese_is_unchanged()
    {
        Assert.Equal("The steaming creek", AdventureEngine.HeaderForm("the steaming creek", "en"));
        Assert.Equal("Socket Village", AdventureEngine.HeaderForm("Socket Village", "en"));
        Assert.Equal("someone's forgotten board".Length, AdventureEngine.HeaderForm("someone's forgotten board", "en").Length);
        Assert.Equal("湯けむりの沢", AdventureEngine.HeaderForm("湯けむりの沢", "ja"));
        Assert.Equal("", AdventureEngine.HeaderForm("", "en"));

        // 本文の全部の町と行き先で
        foreach (var town in TestData.Book.Towns)
            foreach (var name in town.Places.Select(p => p.Name).Prepend(town.Name))
            {
                string en = AdventureEngine.HeaderForm(name.En, "en");
                Assert.True(char.IsUpper(en[0]), en);
                Assert.Equal(name.En[1..], en[1..]);
                Assert.Equal(name.Ja, AdventureEngine.HeaderForm(name.Ja, "ja"));
            }
    }

    [Fact]
    public void The_engine_gives_the_header_form_of_where_the_party_is()
    {
        var state = new PartyState();
        state.Migrate();
        var e = new AdventureEngine(TestData.Book, state, new Random(1));
        var town = TestData.Book.Towns[1];
        state.Adventure!.Town = 1;
        state.Adventure.Mode = AdventureMode.Out;
        state.Adventure.Place = town.Places[0].Id;
        Assert.Equal(town.Places[0].Name.En, e.PlaceName("en"));
        Assert.Equal(AdventureEngine.HeaderForm(town.Places[0].Name.En, "en"), e.PlaceTitle("en"));
        Assert.Equal(town.Places[0].Name.Ja, e.PlaceTitle("ja"));
    }
}
