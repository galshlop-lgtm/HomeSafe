using System.Text.Json;
using HomeSafe.Shared;

namespace HomeSafe.Server;

public sealed class State
{
    public List<UserRow> Users { get; set; } = [];
    public List<SessionRow> Sessions { get; set; } = [];
    public List<InviteRow> Invites { get; set; } = [];
    public List<ReturnRow> Requests { get; set; } = [];
    public List<Delivery> Outbox { get; set; } = [];
}

public sealed class UserRow
{
    public required Account Account { get; set; }
    public required string Email { get; init; }
    public required string PasswordHash { get; init; }
    public Guid? ParentId { get; set; }
    public bool Sharing { get; set; }
    public HomeArea? Home { get; set; }
    public int[] Reminders { get; set; } = [30, 10];
    public DeviceInput? Device { get; set; }
}

public sealed record SessionRow(Guid UserId, string TokenHash, DateTimeOffset ExpiresAt);
public sealed record InviteRow(Guid ParentId, string CodeHash, DateTimeOffset ExpiresAt);

public sealed class ReturnRow
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ParentId { get; init; }
    public required Guid ChildId { get; init; }
    public required HomeArea Home { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset Deadline { get; set; }
    public ReturnStatus Status { get; set; }
    public int Revision { get; set; } = 1;
    public int[] SentReminders { get; set; } = [];
    public LocationFix? Latest { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? AlarmAt { get; set; }
    public int EscalationStage { get; set; }
    public bool ParentAcknowledged { get; set; }
    public bool ChildAcknowledged { get; set; }
}

public sealed class Delivery
{
    public required Guid Recipient { get; init; }
    public required AlertMessage Message { get; init; }
    public required DateTimeOffset NextTry { get; set; }
    public int Attempts { get; set; }
    public bool Accepted { get; set; }
}

public sealed class Store
{
    private readonly object gate = new();
    private readonly string path;
    private State current;

    public Store(IConfiguration config)
    {
        path = Path.GetFullPath(config["HomeSafe:DataFile"] ?? "data/homesafe.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        current = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(path))! : new();
    }

    public T Read<T>(Func<State, T> operation)
    {
        lock (gate) return operation(current);
    }

    public T Write<T>(Func<State, T> operation, Func<T, bool>? changed = null)
    {
        lock (gate)
        {
            // Stage changes privately so failed validation or a failed write cannot leak partial state.
            var draft = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(current))!;
            T result = operation(draft);
            if (changed?.Invoke(result) == false) return result;

            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, draft);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            current = draft;
            return result;
        }
    }
}

public sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
