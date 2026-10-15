using Blackglass.Accounts.Domain.Catalogue;
using Blackglass.Accounts.Domain.Ids;
using Blackglass.Accounts.Domain.Inventory;
using Blackglass.Accounts.Domain.Tests.Catalogue;

namespace Blackglass.Accounts.Domain.Tests.Inventory;

public class StarterPlannerTests
{
    const long Account = 7;

    static IReadOnlyList<string> AllRefs(StarterPlan plan) =>
        plan.Operatives.Select(o => o.OriginRef).Concat(plan.Instances.Select(i => i.OriginRef)).ToList();

    static CatalogueDocument WithStarter(Starter starter) => Golden.Document() with { Starter = starter };

    [Fact]
    public void References_are_unique_and_follow_StarterRef()
    {
        var plan = StarterPlanner.For(Account, Golden.Document());

        Assert.Equal([new PlannedOperative(Golden.One, StarterRef.Operative(Account, Golden.One))], plan.Operatives);
        Assert.Equal(
        [
            new PlannedInstance("item.alpha-rifle", Golden.One, "weapon", StarterRef.OperativeItem(Account, Golden.One, "item.alpha-rifle", 0)),
            new PlannedInstance("item.beta-vest", null, null, StarterRef.StashItem(Account, "item.beta-vest", 0)),
        ], plan.Instances);
        Assert.Equal([new PlannedStack(Golden.One, "item.gamma-kit", 2), new PlannedStack(null, "item.gamma-kit", 4)], plan.Stacks);
        Assert.Equal(AllRefs(plan).Count, AllRefs(plan).Distinct().Count());
    }

    [Fact]
    public void Identical_items_get_distinct_references()
    {
        var plan = StarterPlanner.For(Account, WithStarter(new Starter(
            [new StarterOperative(Golden.One, [new("item.alpha-rifle", 2, false), new("item.alpha-rifle", 1, true)])],
            [new StashGrant("item.alpha-rifle", 2)])));

        var bag = plan.Instances.Where(i => i.OperativeDefinitionId == Golden.One).ToList();
        Assert.Equal([0, 1, 2], bag.Select(i => i.OriginRef).Select(r => int.Parse(r[(r.LastIndexOf('n') + 1)..])).ToList());
        Assert.Equal(AllRefs(plan).Count, AllRefs(plan).Distinct().Count());
        Assert.Equal(["starter:a7:stash:item:item.alpha-rifle:n0", "starter:a7:stash:item:item.alpha-rifle:n1"],
            plan.Instances.Where(i => i.OperativeDefinitionId is null).Select(i => i.OriginRef).ToList());
    }

    [Fact]
    public void Equip_marks_the_lowest_k_even_when_a_later_line_asks()
    {
        var plan = StarterPlanner.For(Account, WithStarter(new Starter(
            [new StarterOperative(Golden.One, [new("item.alpha-rifle", 1, false), new("item.alpha-rifle", 1, true)])], [])));

        Assert.Equal(["weapon", null], plan.Instances.Select(i => i.EquippedSlot).ToList());
    }

    [Fact]
    public void Reordering_grants_of_different_items_keeps_every_reference()
    {
        StarterGrant[] first = [new("item.alpha-rifle", 2, true), new("item.beta-vest", 1, true), new("item.delta-boots", 1, false)];
        var reordered = first.Reverse().ToArray();

        var a = StarterPlanner.For(Account, WithStarter(new Starter([new StarterOperative(Golden.One, first)], [])));
        var b = StarterPlanner.For(Account, WithStarter(new Starter([new StarterOperative(Golden.One, reordered)], [])));

        Assert.Equal(a.Instances.Order(Comparer()).ToList(), b.Instances.Order(Comparer()).ToList());
        Assert.Equal(4, a.Instances.Count);
    }

    static IComparer<PlannedInstance> Comparer() => Comparer<PlannedInstance>.Create((x, y) => string.CompareOrdinal(x.OriginRef, y.OriginRef));

    [Fact]
    public void Different_accounts_get_different_references()
    {
        var one = AllRefs(StarterPlanner.For(1, Golden.Document()));
        var two = AllRefs(StarterPlanner.For(2, Golden.Document()));

        Assert.Equal(one.Count, two.Count);
        Assert.Empty(one.Intersect(two));
    }

    [Fact]
    public void Stackables_sum_per_destination_and_keep_the_order_of_first_appearance()
    {
        var plan = StarterPlanner.For(Account, WithStarter(new Starter(
            [new StarterOperative(Golden.One, [new("item.gamma-kit", 2, false), new("item.alpha-rifle", 1, false), new("item.gamma-kit", 5, false)])],
            [new StashGrant("item.gamma-kit", 1), new StashGrant("item.gamma-kit", 1)])));

        Assert.Equal([new PlannedStack(Golden.One, "item.gamma-kit", 7), new PlannedStack(null, "item.gamma-kit", 2)], plan.Stacks);
    }

    [Fact]
    public void The_committed_catalogue_plans_without_error()
    {
        var document = GameCatalogue.Embedded.Document;
        var plan = StarterPlanner.For(Account, document);

        Assert.Equal(document.Starter.Operatives.Select(o => o.OperativeId).ToList(), plan.Operatives.Select(o => o.DefinitionId).ToList());
        Assert.Equal(AllRefs(plan).Count, AllRefs(plan).Distinct().Count());
        Assert.All(plan.Instances, i => Assert.NotNull(GameCatalogue.Embedded.FindItem(i.DefinitionId)));
    }

    [Fact]
    public void An_operative_missing_from_the_catalogue_is_refused()
    {
        var starter = new Starter([new StarterOperative(Golden.Two + "x", [])], []);
        Assert.Throws<InvalidOperationException>(() => StarterPlanner.For(Account, WithStarter(starter)));
    }

    [Fact]
    public void A_quantity_below_one_is_refused()
    {
        var starter = new Starter([new StarterOperative(Golden.One, [new("item.alpha-rifle", 0, false)])], []);
        Assert.Throws<InvalidOperationException>(() => StarterPlanner.For(Account, WithStarter(starter)));
    }

    [Fact]
    public void Equipping_a_stackable_is_refused()
    {
        var kit = new CatalogueItem("item.gamma-kit", ItemCategory.Consumable, ItemSlot.Utility, true, 3, 10);
        var document = Golden.Document() with { Items = Golden.Document().Items.Select(i => i.Id == kit.Id ? kit : i).ToList() };
        var starter = new Starter([new StarterOperative(Golden.One, [new("item.gamma-kit", 1, true)])], []);
        Assert.Throws<InvalidOperationException>(() => StarterPlanner.For(Account, document with { Starter = starter }));
    }
}
