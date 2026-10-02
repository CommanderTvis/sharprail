using System.Text;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.SpecDialect;

internal static partial class SpecTools
{
    private static async ValueTask<PluginToolResult> CreateAsync(SpecCreateParams parameters, string cwd, CancellationToken ct)
    {
        var resolved = SpecIndex.ResolvePath(cwd, parameters.Path);
        if (resolved.Error is { } error) return Error(error);
        if (File.Exists(resolved.Absolute) || Directory.Exists(resolved.Absolute)) return Error($"File already exists: {resolved.Relative}");
        if ((await Index(cwd).FilesAsync(ct)).Any(file => file.Id == parameters.Id)) return Error($"Spec id \"{parameters.Id}\" is already in use.");
        var fields = new Dictionary<string, object> { ["id"] = parameters.Id, ["type"] = EnumName(parameters.Type) };
        if (parameters.Status is { } status) fields["status"] = EnumName(status);
        fields["title"] = parameters.Title;
        if (parameters.Parent is { } parent) fields["parent"] = parent;
        foreach (var (key, values) in new (string, IReadOnlyList<string>?)[] { ("depends-on", parameters.DependsOn), ("references", parameters.References),
            ("implements", parameters.Implements), ("covers", parameters.Covers), ("tags", parameters.Tags) })
            if (values is { Count: > 0 }) fields[key] = values;
        string[] headings = parameters.Type switch
        {
            SpecType.GoalAndRequirements => ["Goal", "Scope"],
            SpecType.ArchitectureDesign => ["Drivers", "Decisions", "Invariants", "Out of scope"],
            SpecType.TaskSpec => ["Purpose", "Open items"],
            _ => ["Responsibility", "Boundary"]
        };
        var content = SpecFrontmatter.Serialize(fields) + "\n" + string.Join('\n', headings.Select(heading => "## " + heading + "\n"));
        if (SpecFrontmatter.Parse(content)?.IsSpec != true) return Error($"Refusing to write {resolved.Relative}: the frontmatter would not be a spec (id and type must be non-empty).");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.Absolute!)!);
            await using var output = new FileStream(resolved.Absolute!, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await output.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
        }
        catch (IOException exception) { return Error($"Failed to write {resolved.Relative}: {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { return Error($"Failed to write {resolved.Relative}: {exception.Message}"); }
        return Result($"Created {resolved.Relative} (id: {parameters.Id}).", new { Path = resolved.Relative, parameters.Id });
    }

    private static async ValueTask<PluginToolResult> UpdateAsync(SpecUpdateParams parameters, string cwd, CancellationToken ct)
    {
        var file = (await Index(cwd).FilesAsync(ct)).FirstOrDefault(file => file.Id == parameters.Id);
        if (file is null) return Error($"No spec with id \"{parameters.Id}\".");
        var result = file.Frontmatter.Update(parameters);
        if (result.Error is { } error) return Error(error);
        try { await File.WriteAllTextAsync(Path.Combine(cwd, file.Path), result.Content, new UTF8Encoding(false), ct); }
        catch (IOException exception) { return Error($"Failed to write {file.Path}: {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { return Error($"Failed to write {file.Path}: {exception.Message}"); }
        return Result($"Updated frontmatter of {file.Path} (id: {parameters.Id}).", new { parameters.Id, file.Path });
    }

    private static async ValueTask<PluginToolResult> DeleteAsync(SpecDeleteParams parameters, string cwd, CancellationToken ct)
    {
        var file = (await Index(cwd).FilesAsync(ct)).FirstOrDefault(file => file.Id == parameters.Id);
        if (file is null) return Error($"No spec with id \"{parameters.Id}\".");
        try { File.Delete(Path.Combine(cwd, file.Path)); }
        catch (IOException exception) { return Error($"Failed to delete {file.Path}: {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { return Error($"Failed to delete {file.Path}: {exception.Message}"); }
        return Result($"Deleted {file.Path} (id: {parameters.Id}).", new { parameters.Id, file.Path });
    }
}