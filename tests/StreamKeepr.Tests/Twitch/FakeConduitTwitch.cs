using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace StreamKeepr.Tests.Twitch;

/// <summary>
/// A stateful stand-in for the Twitch token endpoint and the Helix conduit, shard, and
/// EventSub subscription endpoints, shaped after the public API reference.
/// </summary>
internal sealed class FakeConduitTwitch : HttpMessageHandler
{
    private readonly object _gate = new();
    private int _nextId;
    private int _tokens;

    public List<string> Conduits { get; } = [];

    /// <summary>Shard 0 session per conduit.</summary>
    public Dictionary<string, string> ShardSessions { get; } = [];

    public List<FakeSubscription> Subscriptions { get; } = [];

    public List<FakeRequest> Requests { get; } = [];

    /// <summary>Broadcaster IDs whose subscription create is refused with 400.</summary>
    public HashSet<string> RefusedUserIds { get; } = [];

    public int PageSize { get; set; } = 100;

    public int MaxTotalCost { get; set; } = 10_000;

    /// <summary>When set and non-null for a request, its response replaces the fake's own.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    /// <summary>Completes after each shard PATCH, with the assigned session ID.</summary>
    public event Action<string>? ShardAssigned;

    public string CurrentToken => $"app-token-{_tokens}";

    public FakeSubscription Add(string type, string userId, string conduitId, string status = "enabled")
    {
        lock (_gate)
        {
            var subscription = new FakeSubscription($"sub-{++_nextId}", type, userId, conduitId, status);
            Subscriptions.Add(subscription);
            return subscription;
        }
    }

    public string AddConduit()
    {
        lock (_gate)
        {
            string id = $"conduit-{++_nextId}";
            Conduits.Add(id);
            return id;
        }
    }

    public FakeRequest[] RequestsMatching(HttpMethod method, string path)
    {
        lock (_gate)
            return Requests.Where(r => r.Method == method && r.Path == path).ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new FakeRequest(
            request.Method,
            request.RequestUri!.AbsolutePath.TrimStart('/'),
            HttpUtility.ParseQueryString(request.RequestUri.Query),
            body,
            request.Headers.Authorization?.Parameter);
        lock (_gate)
            Requests.Add(recorded);

        if (Override?.Invoke(request) is { } overridden)
            return overridden;

        string? assigned = null;
        HttpResponseMessage response;
        lock (_gate)
            response = Route(recorded, out assigned);

        if (assigned is not null)
            ShardAssigned?.Invoke(assigned);
        return response;
    }

    private HttpResponseMessage Route(FakeRequest r, out string? assignedSession)
    {
        assignedSession = null;
        if (r.Path == "oauth2/token")
        {
            _tokens++;
            return Json(HttpStatusCode.OK, $"{{\"access_token\":\"{CurrentToken}\",\"expires_in\":3600}}");
        }

        if (r.Token != CurrentToken)
            return Error(HttpStatusCode.Unauthorized, "Invalid OAuth token");

        switch (r.Method.Method, r.Path)
        {
            case ("GET", "helix/eventsub/conduits"):
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["data"] = new JsonArray(Conduits
                        .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["shard_count"] = 1 })
                        .ToArray()),
                });

            case ("POST", "helix/eventsub/conduits"):
                if (Conduits.Count >= 5)
                    return Error(HttpStatusCode.TooManyRequests, "conduit limit reached");
                int shards = JsonNode.Parse(r.Body)!["shard_count"]!.GetValue<int>();
                string created = $"conduit-{++_nextId}";
                Conduits.Add(created);
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["data"] = new JsonArray(new JsonObject { ["id"] = created, ["shard_count"] = shards }),
                });

            case ("DELETE", "helix/eventsub/conduits"):
                return Conduits.Remove(r.Query["id"]!)
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Error(HttpStatusCode.NotFound, "conduit not found");

            case ("PATCH", "helix/eventsub/conduits/shards"):
                {
                    JsonNode patch = JsonNode.Parse(r.Body)!;
                    string conduit = patch["conduit_id"]!.GetValue<string>();
                    if (!Conduits.Contains(conduit))
                        return Error(HttpStatusCode.NotFound, "conduit not found");
                    JsonNode shard = patch["shards"]![0]!;
                    string session = shard["transport"]!["session_id"]!.GetValue<string>();
                    ShardSessions[conduit] = session;
                    assignedSession = session;
                    return Json(HttpStatusCode.Accepted, new JsonObject
                    {
                        ["data"] = new JsonArray(shard.DeepClone()),
                        ["errors"] = new JsonArray(),
                    });
                }

            case ("GET", "helix/eventsub/subscriptions"):
                return ListSubscriptions(r);

            case ("POST", "helix/eventsub/subscriptions"):
                return CreateSubscription(r);

            case ("DELETE", "helix/eventsub/subscriptions"):
                return Subscriptions.RemoveAll(s => s.Id == r.Query["id"]) > 0
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Error(HttpStatusCode.NotFound, "subscription not found");
        }

        return Error(HttpStatusCode.NotFound, $"no route {r.Method} {r.Path}");
    }

    private HttpResponseMessage ListSubscriptions(FakeRequest r)
    {
        IEnumerable<FakeSubscription> matching = Subscriptions;
        if (r.Query["conduit_id"] is { } conduit)
            matching = matching.Where(s => s.ConduitId == conduit);

        int start = int.TryParse(r.Query["after"], out int after) ? after : 0;
        FakeSubscription[] all = matching.ToArray();
        FakeSubscription[] page = all.Skip(start).Take(PageSize).ToArray();
        int next = start + page.Length;

        var json = new JsonObject
        {
            ["data"] = new JsonArray(page.Select(ToJson).ToArray()),
            ["total"] = Subscriptions.Count,
            ["total_cost"] = Subscriptions.Count,
            ["max_total_cost"] = MaxTotalCost,
            ["pagination"] = next < all.Length
                ? new JsonObject { ["cursor"] = next.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                : new JsonObject(),
        };
        return Json(HttpStatusCode.OK, json);
    }

    private HttpResponseMessage CreateSubscription(FakeRequest r)
    {
        JsonNode request = JsonNode.Parse(r.Body)!;
        string type = request["type"]!.GetValue<string>();
        string userId = request["condition"]!["broadcaster_user_id"]!.GetValue<string>();
        JsonNode transport = request["transport"]!;
        if (transport["method"]!.GetValue<string>() != "conduit")
            return Error(HttpStatusCode.BadRequest, "transport must be conduit for an app token");

        string conduit = transport["conduit_id"]!.GetValue<string>();
        if (RefusedUserIds.Contains(userId))
            return Error(HttpStatusCode.BadRequest, "invalid broadcaster");

        if (Subscriptions.Any(s => s.Type == type && s.UserId == userId && s.ConduitId == conduit))
            return Error(HttpStatusCode.Conflict, "subscription already exists");

        var subscription = new FakeSubscription($"sub-{++_nextId}", type, userId, conduit, "enabled");
        Subscriptions.Add(subscription);
        return Json(HttpStatusCode.Accepted, new JsonObject
        {
            ["data"] = new JsonArray(ToJson(subscription)),
            ["total"] = Subscriptions.Count,
            ["total_cost"] = Subscriptions.Count,
            ["max_total_cost"] = MaxTotalCost,
        });
    }

    private static JsonNode ToJson(FakeSubscription s) => new JsonObject
    {
        ["id"] = s.Id,
        ["status"] = s.Status,
        ["type"] = s.Type,
        ["version"] = "1",
        ["condition"] = new JsonObject { ["broadcaster_user_id"] = s.UserId },
        ["transport"] = new JsonObject { ["method"] = "conduit", ["conduit_id"] = s.ConduitId },
        ["cost"] = 1,
    };

    public static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        Json(status, new JsonObject
        {
            ["error"] = status.ToString(),
            ["status"] = (int)status,
            ["message"] = message,
        });

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode json) =>
        Json(status, json.ToJsonString());

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed record FakeSubscription(
    string Id, string Type, string UserId, string ConduitId, string Status);

internal sealed record FakeRequest(
    HttpMethod Method,
    string Path,
    System.Collections.Specialized.NameValueCollection Query,
    string Body,
    string? Token)
{
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
}
