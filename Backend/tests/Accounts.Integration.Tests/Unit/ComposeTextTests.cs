using Blackglass.Accounts.IntegrationTests.Support;

namespace Blackglass.Accounts.IntegrationTests.Unit;

public class ComposeTextTests
{
    [Fact]
    public void List_items_at_the_same_indent_as_their_key_belong_to_the_block()
    {
        string[] lines =
        [
            "services:",
            "  postgres:",
            "    image: postgres:17",
            "    ports:",
            "    - \"5432:5432\"",
            "    restart: unless-stopped",
        ];
        Assert.Equal(["- \"5432:5432\""], ComposeText.Block(lines, "services", "postgres", "ports").Select(l => l.Trim()));
    }

    [Fact]
    public void Indented_list_items_belong_to_the_block()
    {
        string[] lines =
        [
            "services:",
            "  postgres:",
            "    ports:",
            "      - \"5432:5432\"",
            "    restart: unless-stopped",
        ];
        Assert.Equal(["- \"5432:5432\""], ComposeText.Block(lines, "services", "postgres", "ports").Select(l => l.Trim()));
    }

    [Fact]
    public void The_block_ends_at_the_next_sibling_key()
    {
        string[] lines =
        [
            "services:",
            "  postgres:",
            "    ports:",
            "    - \"5432:5432\"",
            "    restart: unless-stopped",
        ];
        var (start, end) = ComposeText.Find(lines, "services", "postgres", "ports")!.Value;
        Assert.Equal((3, 4), (start, end));
    }
}
