using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Zorofin.Companion;

/// <summary>Result of one companion call. Status 0 means the companion could not be reached.</summary>
/// <param name="Status">HTTP status, or 0 when unreachable.</param>
/// <param name="Body">Parsed JSON body, if any.</param>
public sealed record CompanionResponse(int Status, JsonNode? Body);
