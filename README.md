# StreamKeepr

## Running the new web app

The new app in `src/StreamKeepr` validates its configuration at startup, so it needs Twitch app credentials even while it is only a shell. For local development:

```bash
dotnet user-secrets --project src/StreamKeepr set "Twitch:ClientId" "<client-id>"
dotnet user-secrets --project src/StreamKeepr set "Twitch:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/StreamKeepr set "Storage:RecordingsRoot" "$PWD/recordings"
dotnet run --project src/StreamKeepr
```

Then open <http://localhost:5080>. Tests run with `dotnet test tests/StreamKeepr.Tests`.

The old worker still builds beside the new app until the migration switch.
