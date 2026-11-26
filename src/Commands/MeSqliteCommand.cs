using System.ComponentModel;
using Linkedin.Console.Infrastructure;
using Linkedin.Console.Snapshots;
using Spectre.Console.Cli;

namespace Linkedin.Console.Commands;

public sealed class MeSqliteCommand : LinkedInCommand<MeSqliteCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("--from <DIR>")]
        [Description("Export folder written by 'linkedin me snapshot --out-dir'")]
        public string? From { get; init; }

        [CommandOption("--changes <DIR>")]
        [Description("Folder written by 'linkedin me sync', to include activity as a changelog table")]
        public string? Changes { get; init; }

        [CommandOption("--out <FILE>")]
        [Description("The database file to create")]
        public string? Out { get; init; }

        [CommandOption("--force")]
        [Description("Replace the database file if it exists")]
        public bool Force { get; init; }
    }

    protected override void Check(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.From)) throw LinkedInException.Invalid("--from is required.");
        if (string.IsNullOrWhiteSpace(settings.Out)) throw LinkedInException.Invalid("--out is required.");
    }

    protected override Task<object?> RunAsync(Settings settings, CancellationToken ct)
    {
        var output = Path.GetFullPath(settings.Out!);
        var result = SqliteBuilder.Build(
            Path.GetFullPath(settings.From!),
            settings.Changes is null ? null : Path.GetFullPath(settings.Changes),
            output, settings.Force, OutputHelper.Status);

        return Task.FromResult<object?>(new Dictionary<string, object?>
        {
            ["file"] = output,
            ["tables"] = result.Tables,
            ["rows"] = result.Rows,
            ["events"] = settings.Changes is null ? null : result.Events,
            ["exportComplete"] = result.ExportComplete,
            ["warning"] = result.ExportComplete
                ? null
                : "The export did not finish, so this database only holds its finished domains."
        });
    }
}
