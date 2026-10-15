using System.Text.RegularExpressions;

namespace Blackglass.Accounts.IntegrationTests.Unit;

/// <summary>Spec §13.2: parameterized SQL only. A source audit bans string-built SQL in src/.</summary>
public partial class SqlSourceAuditTests
{
    // An interpolated literal is SQL-shaped when a SQL keyword opens it (a statement or a fragment such as " where …"), or
    // when a statement keyword is followed later in the same literal by SQL structure ("… from … where …"). Prose such
    // as $"{name} is not set." is neither.
    [GeneratedRegex(@"(\$@?""|@\$"")\s*(select|insert|update|delete|create|alter|drop|grant|revoke|truncate|set|with|where|from|values|and|or|order|returning|join)\b|(\$@?""|@\$"")[^""]*\b(select|insert|update|delete|create|alter|drop|grant|revoke|truncate|set|from)\b[^""]*\b(from|into|set|where|table|values)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Interpolated();

    [GeneratedRegex(@"""[^""]*\b(select|insert|update|delete|create|alter|drop|grant|revoke|truncate|set)\b[^""]*""\s*\+\s*[A-Za-z_(]|[A-Za-z_)]\s*\+\s*""[^""]*\b(where|values|from|set)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Concatenated();

    public static IReadOnlyList<string> Find(string fileName, string source)
    {
        if (!source.Contains("using Dapper;") && !source.Contains("using Npgsql;")) return [];
        var findings = new List<string>();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (Interpolated().IsMatch(lines[i]) || Concatenated().IsMatch(lines[i]))
                findings.Add($"{fileName}:{i + 1}: {lines[i].Trim()}");
        return findings;
    }

    [Theory]
    [InlineData("using Dapper;\nconn.ExecuteAsync($\"select * from t where id = {id}\");", 1)]
    [InlineData("using Dapper;\nconn.ExecuteAsync(\"select * from t where id = \" + id);", 1)]
    [InlineData("using Npgsql;\nvar sql = $@\"update t set x = {x}\";", 1)]
    [InlineData("using Npgsql;\nvar sql = @$\"delete from t where x = {x}\";", 1)]
    [InlineData("using Dapper;\nawait conn.ExecuteAsync($\"set local lock_timeout = {ms}\");", 1)]
    [InlineData("using Dapper;\nconn.ExecuteAsync(\"select * from t where id = @id\", new { id });", 0)]
    [InlineData("using Dapper;\nerror.WriteLine($\"Configuration error: {e.Message}\");", 0)]
    [InlineData("using Dapper;\nvar sql = \"select * from t\" + $\" where id = {id}\";", 1)]
    [InlineData("using Dapper;\nvar sql = $\"{verb} from t where id = {id}\";", 1)]
    [InlineData("using Dapper;\nthrow new X($\"{name} is not set.\");", 0)]
    [InlineData("using Dapper;\nlog($\"Refusing to start: schema {state}: {detail}\");", 0)]
    [InlineData("var s = $\"select {x}\";", 0)] // no Dapper or Npgsql in the file: not SQL
    public void The_audit_finds_string_built_sql(string source, int expected) =>
        Assert.Equal(expected, Find("sample.cs", source).Count);

    [Fact]
    public void No_source_file_builds_sql_from_strings()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "source");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith("AccountAdminStore.cs", StringComparison.Ordinal)); // the copy is complete
        var findings = files.SelectMany(f => Find(Path.GetRelativePath(root, f), File.ReadAllText(f))).ToList();
        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }
}
