using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using System.Text;

class Program
{
  static async Task<int> Main(string[] args)
  {
    var config = new ConfigurationBuilder()
      .SetBasePath(Directory.GetCurrentDirectory())
      .AddJsonFile("appsettings.json")
      .Build();

    var apiKey      = config["Airtable:ApiKey"]        ?? throw new Exception("Airtable:ApiKey not configured");
    var baseId      = config["Airtable:BaseId"]        ?? throw new Exception("Airtable:BaseId not configured");
    var csvPath     = config["Import:CsvPath"]         ?? throw new Exception("Import:CsvPath not configured");
    var targetTable = config["Import:TargetTable"]     ?? throw new Exception("Import:TargetTable not configured");
    var linkField   = config["Import:LinkField"]       ?? throw new Exception("Import:LinkField not configured");
    var linkedTable = config["Import:LinkedTable"]     ?? throw new Exception("Import:LinkedTable not configured");
    var matchField  = config["Import:LinkMatchField"]  ?? throw new Exception("Import:LinkMatchField not configured");

    bool create = args.Any(a => a.Equals("create", StringComparison.OrdinalIgnoreCase));

    Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                  CSV → Airtable Import                     ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
    Console.WriteLine($"  CSV:    {csvPath}");
    Console.WriteLine($"  Target: {targetTable}");
    Console.WriteLine($"  Link:   {linkField} → {linkedTable}.{matchField}");
    Console.WriteLine($"  Mode:   {(create ? "LIVE (will create records)" : "DRY RUN (pass 'create' arg to apply)")}");
    Console.WriteLine();

    if (!File.Exists(csvPath))
    {
      Console.WriteLine($"✗ CSV file not found: {csvPath}");
      return 1;
    }

    var (header, rows) = ParseCsv(csvPath);
    Console.WriteLine($"✓ Parsed {rows.Count} data rows. Header: [{string.Join(", ", header)}]");

    int linkFieldIndex = Array.IndexOf(header, linkField);
    if (linkFieldIndex < 0)
    {
      Console.WriteLine($"✗ Link field '{linkField}' not found in CSV header");
      return 1;
    }

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

    Console.WriteLine($"Loading {linkedTable} records...");
    var lookup = await BuildLookup(http, baseId, linkedTable, matchField);
    Console.WriteLine($"✓ Loaded {lookup.Count} {linkedTable} records (matched against \"{matchField}\")\n");

    var toCreate = new List<JObject>();
    var skipped = new List<string>();
    for (int i = 0; i < rows.Count; i++)
    {
      var row = rows[i];
      var matchValue = row[linkFieldIndex].Trim();
      if (!lookup.TryGetValue(matchValue, out var recordId))
      {
        skipped.Add($"Row {i + 2}: no {linkedTable} record with {matchField}=\"{matchValue}\"");
        continue;
      }

      var fields = new JObject
      {
        [linkField] = new JArray(recordId)
      };
      for (int c = 0; c < header.Length; c++)
      {
        if (c == linkFieldIndex) continue;
        var val = row[c].Trim();
        if (val.Length > 0) fields[header[c]] = val;
      }
      toCreate.Add(new JObject { ["fields"] = fields });
    }

    Console.WriteLine($"Ready to create: {toCreate.Count}. Skipped: {skipped.Count}.");
    if (skipped.Count > 0)
    {
      Console.WriteLine($"First {Math.Min(10, skipped.Count)} skipped rows:");
      foreach (var s in skipped.Take(10)) Console.WriteLine($"  {s}");
      Console.WriteLine();
    }

    if (!create)
    {
      Console.WriteLine();
      Console.WriteLine("DRY RUN — no records created. Re-run with: dotnet run -- create");
      return 0;
    }

    if (toCreate.Count == 0)
    {
      Console.WriteLine("Nothing to create.");
      return 0;
    }

    int created = 0;
    int failed  = 0;
    var errors  = new List<string>();
    var createUrl = $"https://api.airtable.com/v0/{baseId}/{Uri.EscapeDataString(targetTable)}";

    for (int batchStart = 0; batchStart < toCreate.Count; batchStart += 10)
    {
      var batch = toCreate.Skip(batchStart).Take(10).ToList();
      var body = new JObject { ["records"] = new JArray(batch.Cast<object>().ToArray()) };
      var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");

      var resp = await http.PostAsync(createUrl, content);
      var respBody = await resp.Content.ReadAsStringAsync();

      if (resp.IsSuccessStatusCode)
      {
        created += batch.Count;
        Console.Write($"\r  Created {created}/{toCreate.Count}...   ");
      }
      else
      {
        failed += batch.Count;
        var msg = $"Batch rows {batchStart + 2}-{batchStart + 1 + batch.Count}: HTTP {(int)resp.StatusCode} — {respBody}";
        errors.Add(msg);
        Console.WriteLine();
        Console.WriteLine($"  ✗ {msg}");
      }

      await Task.Delay(250); // stay under 5 req/sec
    }

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("─────────────────────────────────────");
    Console.WriteLine($"  Total rows: {rows.Count}");
    Console.WriteLine($"  Created:    {created}");
    Console.WriteLine($"  Failed:     {failed}");
    Console.WriteLine($"  Skipped:    {skipped.Count}");
    Console.WriteLine("─────────────────────────────────────");

    if (errors.Count > 0)
    {
      Console.WriteLine();
      Console.WriteLine("Errors:");
      foreach (var e in errors) Console.WriteLine($"  {e}");
    }

    return failed > 0 ? 1 : 0;
  }

  static (string[] header, List<string[]> rows) ParseCsv(string path)
  {
    var text = File.ReadAllText(path);
    if (text.Length > 0 && text[0] == '﻿') text = text[1..];

    var rows = new List<string[]>();
    var row = new List<string>();
    var field = new StringBuilder();
    bool inQuotes = false;

    for (int i = 0; i < text.Length; i++)
    {
      char c = text[i];
      if (inQuotes)
      {
        if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
        {
          field.Append('"');
          i++;
        }
        else if (c == '"')
        {
          inQuotes = false;
        }
        else
        {
          field.Append(c);
        }
      }
      else
      {
        if (c == '"') inQuotes = true;
        else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
        else if (c == '\r') { /* skip */ }
        else if (c == '\n') { row.Add(field.ToString()); rows.Add(row.ToArray()); row.Clear(); field.Clear(); }
        else field.Append(c);
      }
    }
    if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }

    if (rows.Count == 0) throw new Exception("CSV is empty");
    var header = rows[0].Select(h => h.Trim()).ToArray();
    var dataRows = rows.Skip(1).Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
    return (header, dataRows);
  }

  static async Task<Dictionary<string, string>> BuildLookup(HttpClient http, string baseId, string table, string matchField)
  {
    var lookup = new Dictionary<string, string>();
    string? offset = null;
    do
    {
      var url = $"https://api.airtable.com/v0/{baseId}/{Uri.EscapeDataString(table)}?fields%5B%5D={Uri.EscapeDataString(matchField)}";
      if (offset != null) url += $"&offset={Uri.EscapeDataString(offset)}";

      var resp = await http.GetAsync(url);
      resp.EnsureSuccessStatusCode();
      var body = await resp.Content.ReadAsStringAsync();
      var json = JObject.Parse(body);

      foreach (var rec in json["records"]!.OfType<JObject>())
      {
        var id = rec["id"]!.ToString();
        var fieldVal = rec["fields"]?[matchField]?.ToString();
        if (fieldVal != null) lookup[fieldVal.Trim()] = id;
      }

      offset = json["offset"]?.ToString();
      await Task.Delay(200);
    } while (offset != null);

    return lookup;
  }
}
