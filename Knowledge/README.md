# Local knowledge workflow

The application has no automatic online lookup or telemetry. A technician can prepare a UTF-8 JSON bundle from reviewed technical material and import it from **Universal Diagnostics**. The importer accepts schema version 1 only, limits the file to 2 MB, requires HTTPS sources without URL query credentials, rejects invalid/expired entries, and stores all imports as `PendingReview`.

Example bundle:

```json
{
  "schemaVersion": 1,
  "entries": [
    {
      "id": "vendor-example-001",
      "expiresAt": "2027-01-01T00:00:00Z",
      "windowsVersion": "10.0.26300",
      "minimumWindowsBuild": 26000,
      "maximumWindowsBuild": 27000,
      "title": "Short documented symptom title",
      "summary": "Reviewed explanation or resolution; this is text, never an executable command.",
      "verification": "Non-destructive test that verifies the stated resolution.",
      "sources": ["https://learn.microsoft.com/windows/"]
    }
  ]
}
```

An authorized local reviewer must explicitly approve a candidate in the UI. Approval requires a future expiry, HTTPS provenance, a resolution summary, verification steps and a review note. Entries outside their Windows-version/build compatibility or past expiry are excluded from matching. Text matches suggest reading a knowledge entry; they do not establish that it caused the symptom.

Cases and knowledge live in `%LOCALAPPDATA%\WinFixPro\0.2\Knowledge`. Case fields receive basic redaction of the current computer name, account name and user-profile path. Technicians must still omit secrets and review content before sharing.

The diagnostic plugin contract is documented in `..\Plugins\README.md`. Imported knowledge is data-only; diagnostic plugins, by contrast, are executable PowerShell and require explicit opt-in.
