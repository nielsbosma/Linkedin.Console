using System.Text;
using Linkedin.Console.Infrastructure;

namespace Linkedin.Console.Outreach;

public sealed record CsvPerson(string Url, string Key, string? FirstName, string? Note, string? Followup);

public sealed record AddResult(int Added, int AlreadyListed, int OverReportedFreeLimit, int WithoutNote, int Obfuscated);

/// <summary>
/// The list people are added from. A bad file is refused whole, naming every line, so a fix and a
/// rerun can't leave half a list behind.
/// </summary>
public static class OutreachCsv
{
    /// <summary>What LinkedIn allows on a note, as reported by several projects.</summary>
    public const int NoteLimit = 300;

    /// <summary>Reported for free accounts, so it warns rather than refuses.</summary>
    public const int FreeNoteLimit = 200;

    public static List<CsvPerson> Parse(string text)
    {
        // A file saved in an old code page reads as replacement characters, and å would go out as �.
        if (text.Contains('�'))
            throw LinkedInException.Invalid("The file isn't UTF-8, so names and notes would come out garbled. Save it as UTF-8 and try again.");

        var rows = ReadRows(text);
        if (rows.Count == 0) throw LinkedInException.Invalid("The file is empty. Its first line needs a url column.");

        var header = rows[0].Fields.Select(f => f.Trim()).ToList();
        var url = header.FindIndex(h => h.Equals("url", StringComparison.OrdinalIgnoreCase));
        if (url < 0) throw LinkedInException.Invalid("The file has no url column. Columns: url, firstName, note, followup.");

        var firstName = header.FindIndex(h => h.Equals("firstName", StringComparison.OrdinalIgnoreCase));
        var note = header.FindIndex(h => h.Equals("note", StringComparison.OrdinalIgnoreCase));
        var followup = header.FindIndex(h => h.Equals("followup", StringComparison.OrdinalIgnoreCase));

        var people = new List<CsvPerson>();
        var problems = new List<string>();

        foreach (var (line, fields) in rows.Skip(1))
        {
            if (fields.Count > header.Count)
            {
                problems.Add($"line {line}: {fields.Count} columns where the first line has {header.Count}. Text with a comma needs double quotes.");
                continue;
            }

            string? Cell(int index) =>
                index >= 0 && index < fields.Count && !string.IsNullOrWhiteSpace(fields[index]) ? fields[index].Trim() : null;

            string canonical, key;
            try
            {
                (canonical, key) = LinkedInUrl.CanonicalProfile(Cell(url) ?? "");
            }
            catch (ArgumentException)
            {
                problems.Add($"line {line}: not a LinkedIn profile link.");
                continue;
            }

            var noteText = Cell(note);
            if (noteText?.Length > NoteLimit)
            {
                problems.Add($"line {line}: the note has {noteText.Length} characters, and LinkedIn allows {NoteLimit}.");
                continue;
            }

            people.Add(new CsvPerson(canonical, key, Cell(firstName), noteText, Cell(followup)));
        }

        if (problems.Count > 0)
            throw new LinkedInException(ErrorCode.InvalidInput,
                $"Nothing was added: the file has {problems.Count} {(problems.Count == 1 ? "problem" : "problems")}.",
                string.Join("\n", problems));

        return people;
    }

    /// <summary>Anyone already listed stays as they are, whatever state they're in.</summary>
    public static AddResult AddTo(OutreachData data, IEnumerable<CsvPerson> people, DateTimeOffset now)
    {
        int added = 0, listed = 0, long200 = 0, noNote = 0, obfuscated = 0;

        foreach (var person in people)
        {
            if (data.Find(person.Key) is not null)
            {
                listed++;
                continue;
            }

            data.People.Add(new OutreachPerson
            {
                Key = person.Key,
                Url = person.Url,
                FirstName = person.FirstName,
                Note = person.Note,
                Followup = person.Followup,
                State = PersonState.Waiting,
                AddedAt = now
            });

            added++;
            if (person.Note is null) noNote++;
            else if (person.Note.Length > FreeNoteLimit) long200++;
            if (person.Key.StartsWith("acoa", StringComparison.Ordinal)) obfuscated++;
        }

        return new AddResult(added, listed, long200, noNote, obfuscated);
    }

    /// <summary>
    /// RFC 4180: notes hold commas, quotes and line breaks. Excel on a Swedish PC separates with
    /// semicolons, which the first line gives away. Line numbers count from where a row starts.
    /// </summary>
    private static List<(int Line, List<string> Fields)> ReadRows(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var separator = !firstLine.Contains(',') && firstLine.Contains(';') ? ';' : ',';

        var rows = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var rowStart = 1;
        var quoted = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            if (fields.Exists(f => !string.IsNullOrWhiteSpace(f))) rows.Add((rowStart, fields));
            fields = [];
            rowStart = line;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '\n') line++;

                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = false;
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == separator) EndField();
            else if (c == '\n')
            {
                line++;
                EndRow();
            }
            else if (c != '\r') field.Append(c);
        }

        if (quoted) throw LinkedInException.Invalid($"The quote opened on line {rowStart} is never closed.");

        EndRow();
        return rows;
    }
}
