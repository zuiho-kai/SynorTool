using System.Text;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "synor-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    async Task<(string Text, string? Image, bool IsError)> Run(string name, object args, CancellationToken token = default) =>
        await AgentTools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), root, _ => { }, token);
    void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    var result = await Run("write", new { path = "nested/a.txt", content = "Alpha\nBeta\nGamma\n" });
    Check(!result.IsError && File.Exists(Path.Combine(root, "nested/a.txt")), "write creates parents");
    result = await Run("read", new { path = "nested/a.txt", offset = 2, limit = 1 });
    Check(result.Text.Contains("Beta") && !result.Text.Contains("Alpha") && result.Text.Contains("offset=3"), "read pagination");
    result = await Run("edit", new { path = "nested/a.txt", edits = new[] { new { oldText = "Alpha", newText = "One" }, new { oldText = "Gamma", newText = "Three" } } });
    Check(!result.IsError && File.ReadAllText(Path.Combine(root, "nested/a.txt")) == "One\nBeta\nThree\n", "edit multiple replacements");
    var before = File.ReadAllBytes(Path.Combine(root, "nested/a.txt"));
    result = await Run("edit", new { path = "nested/a.txt", edits = new[] { new { oldText = "One", newText = "New" }, new { oldText = "One\nBeta", newText = "Overlap" } } });
    Check(result.IsError && File.ReadAllBytes(Path.Combine(root, "nested/a.txt")).SequenceEqual(before), "overlap leaves file unchanged");
    await File.WriteAllTextAsync(Path.Combine(root, "bom.txt"), "“Hello”  \r\nWorld\r\n", new UTF8Encoding(true));
    result = await Run("edit", new { path = "bom.txt", edits = new[] { new { oldText = "\"Hello\"\nWorld", newText = "Hi\nEarth" } } });
    Check(!result.IsError && File.ReadAllBytes(Path.Combine(root, "bom.txt")).AsSpan().StartsWith(new byte[] {239,187,191}) && File.ReadAllText(Path.Combine(root, "bom.txt")).Contains("Hi\r\nEarth"), "fuzzy matching preserves BOM and CRLF");
    await File.WriteAllTextAsync(Path.Combine(root, "duplicate.txt"), "same same");
    result = await Run("edit", new { path = "duplicate.txt", edits = new[] { new { oldText = "same", newText = "other" } } });
    Check(result.IsError && File.ReadAllText(Path.Combine(root, "duplicate.txt")) == "same same", "duplicate match rejected");
    Directory.CreateDirectory(Path.Combine(root, ".git"));
    await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored.txt\n");
    await File.WriteAllTextAsync(Path.Combine(root, "ignored.txt"), "Beta");
    result = await Run("grep", new { pattern = "beta", ignoreCase = true, context = 1 });
    Check(!result.IsError && result.Text.Contains("nested/a.txt:2:Beta") && result.Text.Contains("One") && result.Text.Contains("Three") && !result.Text.Contains("ignored.txt"), "grep case context gitignore");
    result = await Run("grep", new { pattern = "Beta", context = 1, limit = 1, glob = "nested/*.txt" });
    Check(!result.IsError && result.Text.Contains("Three"), "grep preserves context at match limit");
    result = await Run("grep", new { pattern = "[", literal = true });
    Check(!result.IsError && result.Text.Contains("No matches"), "literal grep and no-match handling");
    result = await Run("grep", new { pattern = "[" });
    Check(result.IsError, "invalid regex reported");
    result = await Run("find", new { pattern = "nested/**/*.txt" });
    Check(!result.IsError && result.Text.Contains("nested/a.txt"), "find nested glob");
    result = await Run("find", new { pattern = "ignored.txt" });
    Check(!result.IsError && result.Text.Contains("No matches"), "find respects gitignore");
    result = await Run("ls", new { });
    Check(result.Text.Contains("nested/") && result.Text.Contains(".gitignore"), "ls directories and dotfiles");
    result = await Run("bash", new { command = "printf 'out'; printf 'err' >&2; exit 7" });
    Check(result.IsError && result.Text.Contains("out") && result.Text.Contains("err") && result.Text.Contains("Exit code: 7"), "bash output stderr and exit status");
    result = await Run("bash", new { command = "sleep 20", timeout = 0.1 });
    Check(result.IsError && result.Text.Contains("timed out"), "bash timeout");
    using var cancellation = new CancellationTokenSource(100);
    var cancelled = false;
    try { await Run("bash", new { command = "sleep 20" }, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "bash cancellation");
    var updates = 0;
    result = await AgentTools.ExecuteAsync("bash", JsonSerializer.SerializeToElement(new { command = "for i in 1 2 3; do echo tick; sleep 0.15; done" }), root, _ => updates++, default);
    Check(updates > 0 && !result.IsError, "bash streams incremental output");
    var longText = string.Join('\n', Enumerable.Repeat("abcdef", 2500));
    await File.WriteAllTextAsync(Path.Combine(root, "long.txt"), longText);
    result = await Run("read", new { path = "long.txt" });
    Check(result.Text.Contains("offset=2001"), "read 2000-line truncation");
    result = await Run("read", new { path = Path.Combine(Environment.CurrentDirectory, "Assets/BrandIcon.png") });
    Check(!result.IsError && result.Image?.StartsWith("data:image/png;base64,") == true, "read image attachment");
    Check(AgentTools.ResolvePath("~/file", root).StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "home path expansion");
}
finally { Directory.Delete(root, true); }
