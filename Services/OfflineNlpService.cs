using Catalyst;
using Catalyst.Models;
using Mosaik.Core;

namespace CLIo.Services;

public sealed class OfflineNlpService
{
    private Pipeline? pipeline;
    private bool initialized;

    public async Task EnsureInitializedAsync()
    {
        if (initialized) return;

        Catalyst.Models.English.Register();
        Storage.Current = new DiskStorage(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo", "catalyst"));

        pipeline = await Pipeline.ForAsync(Language.English);
        pipeline.Add(await AveragePerceptronEntityRecognizer.FromStoreAsync(Language.English, 0, "WikiNER"));

        initialized = true;
    }

    public async Task<NlpResult> AnalyzeAsync(string text)
    {
        await EnsureInitializedAsync();
        if (pipeline is null)
            return new NlpResult([], []);

        var doc = new Document(text, Language.English);
        pipeline.ProcessSingle(doc);

        var entities = new List<NlpEntity>();
        foreach (var span in doc.SelectMany(s => s.GetEntities()))
        {
            entities.Add(new NlpEntity(
                span.EntityType.Type,
                span.Value,
                span.Begin,
                span.End));
        }

        var tokens = doc.SelectMany(s => s.Tokens)
            .Select(t => t.Value)
            .ToList();

        return new NlpResult(entities, tokens);
    }
}

public sealed record NlpEntity(string Type, string Value, int Begin, int End);
public sealed record NlpResult(IReadOnlyList<NlpEntity> Entities, IReadOnlyList<string> Tokens);
