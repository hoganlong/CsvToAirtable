# CsvToAirtable

A small .NET 10 console tool that imports a CSV file into an Airtable table, with proper support for **linked-record fields matched by any column** (not just the target table's primary field).

## Why this exists

Airtable's built-in CSV import has a sharp edge: when a CSV column maps to a linked-record field, Airtable will only match values against the **primary field** of the target table. If your CSV holds integer foreign keys but the target table's primary field is, say, a title or name, **every row's link silently fails and no records are created** — no error, no warning. This tool does the lookup explicitly against any field you choose, then sends pre-resolved `recordId` values to the Airtable API so links are unambiguous.

It also surfaces per-batch API errors that the Airtable web UI suppresses.

## Requirements

- .NET 10.0 SDK
- An Airtable Personal Access Token (PAT) with both `data.records:read` and `data.records:write` scopes for the target base

## Setup

1. Copy `appsettings.template.json` to `appsettings.json`.
2. Fill in the values:

| Key | Meaning |
|---|---|
| `Airtable:ApiKey` | Your PAT, including the `pat...` prefix |
| `Airtable:BaseId` | The base ID, e.g. `appXXXXXXXXXXXXXX` |
| `Import:CsvPath` | Absolute path to the input CSV |
| `Import:TargetTable` | Name of the Airtable table to insert rows into |
| `Import:LinkField` | The CSV column / target-table field that is a linked-record field |
| `Import:LinkedTable` | The table that `LinkField` points to |
| `Import:LinkMatchField` | The field **in `LinkedTable`** to match CSV values against (often an autonumber `ID`) |

The CSV's other columns are assumed to map 1:1 by header name to fields in `TargetTable`.

## Usage

Dry run (default — shows what would happen, makes no changes):

```bash
dotnet run
```

Actually create the records:

```bash
dotnet run -- create
```

Print all options:

```bash
dotnet run -- --help    # also: -h, -?, /?, ?
```

Unknown flags (any unrecognized token starting with `-` or `/`) exit with code 1 after printing usage.

## What it does

1. Parses the CSV (RFC-4180 compliant, strips UTF-8 BOM if present).
2. Loads all records from `LinkedTable` and builds a `LinkMatchField → recordId` map.
3. For each CSV row:
   - Looks up `LinkField`'s value in the map. If no match, the row is reported as **skipped** with a reason.
   - Builds an Airtable record with the resolved `recordId` plus every other CSV column as a field.
4. POSTs records in batches of 10 (Airtable's max per `createRecords` call), with a 250 ms gap between batches to stay under the 5 req/sec rate limit.
5. Prints per-batch HTTP errors with the full Airtable response body so failures are diagnosable.

## Output example

```
✓ Parsed 446 data rows. Header: [ARTWORK_ID, URL, PHOTOGRAPHER, VIEW, NOTES]
Loading ARTWORK records...
✓ Loaded 1894 ARTWORK records (matched against "ID")

Ready to create: 446. Skipped: 0.
  Created 446/446...

─────────────────────────────────────
  Total rows: 446
  Created:    446
  Failed:     0
  Skipped:    0
─────────────────────────────────────
```

## Common errors

| Error | Meaning |
|---|---|
| `INVALID_PERMISSIONS_OR_MODEL_NOT_FOUND` (HTTP 403) | Your PAT is missing `data.records:write`, or the base/table isn't in the PAT's allowed list. |
| `Row N: no <Table> record with <Field>="<value>"` (skipped) | The CSV row has a `LinkField` value that doesn't exist in `LinkedTable`. Check the value and the `LinkMatchField`. |
| `UNKNOWN_FIELD_NAME` | A CSV header doesn't match a field name in `TargetTable`. Field names are case-sensitive. |

## Notes

- The token is stored in `appsettings.json`, which is **gitignored**. Never commit your real token.
- Field names in the CSV header must exactly match the Airtable field names (case-sensitive). Whitespace is trimmed.
- Values are sent as strings; Airtable will type-coerce per the target field's type. Single/multi-select fields require the option text to match an existing option exactly.
