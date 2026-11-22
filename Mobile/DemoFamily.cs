using HomeSafe.Shared;

namespace HomeSafe.Mobile;

internal sealed class DemoFamily
{
    private readonly Account parent = new(Guid.NewGuid(), "נועה", FamilyRole.Parent, null);
    private readonly Account[] children =
    [
        new(Guid.NewGuid(), "דניאל", FamilyRole.Child, null),
        new(Guid.NewGuid(), "מאיה", FamilyRole.Child, null)
    ];
    private readonly List<ReturnView> requests = [];
    private HomeArea home = new("בית המשפחה", 32.08091, 34.78057, 100);
    private int[] reminders = [30, 10];
    private bool sharing = true;
    public FamilyRole Role { get; set; }

    public DemoFamily(FamilyRole role)
    {
        Role = role;
        requests.Add(new(Guid.NewGuid(), children[0].Id, children[0].Name, DateTimeOffset.UtcNow.AddMinutes(43),
            home, ReturnStatus.Accepted, 1, null, false, false, null, false));
        requests.Add(new(Guid.NewGuid(), children[1].Id, children[1].Name, DateTimeOffset.UtcNow.AddMinutes(-52),
            home, ReturnStatus.Arrived, 1, DateTimeOffset.UtcNow.AddMinutes(-60), false, false, null, false));
    }

    public FamilyView View()
    {
        foreach (var row in requests.ToArray())
            if (row.Deadline <= DateTimeOffset.UtcNow && row.Status is ReturnStatus.Pending or ReturnStatus.Accepted)
                Update(row with { Status = ReturnStatus.LocationUnavailable });
        bool isParent = Role == FamilyRole.Parent;
        return new(isParent ? parent : children[0], isParent ? null : parent,
            isParent ? children : [], home, sharing, reminders,
            requests.Where(r => isParent || r.ChildId == children[0].Id).ToArray(), false, false);
    }

    public void SaveHome(HomeArea area) => home = area;
    public void SetReminders(int[] minutes) => reminders = minutes;
    public void SetSharing(bool enabled) => sharing = enabled;

    public void SaveReturn(Guid childId, DateTimeOffset deadline, ReturnView? existing)
    {
        var child = children.First(c => c.Id == childId);
        if (existing is null)
            requests.Insert(0, new(Guid.NewGuid(), child.Id, child.Name, deadline, home,
                ReturnStatus.Pending, 1, null, false, false, null, false));
        else
            Update(existing with { Deadline = deadline, Revision = existing.Revision + 1, Status = ReturnStatus.Pending });
    }

    public void Update(ReturnView row)
    {
        int index = requests.FindIndex(r => r.Id == row.Id);
        requests[index] = row;
    }

    public ReturnView TestAlarm()
    {
        var source = requests.First(r => r.ChildId == children[0].Id);
        return source with
        {
            Status = ReturnStatus.Overdue, Deadline = DateTimeOffset.Now.AddMinutes(-1),
            Acknowledged = false, ArrivedAt = null
        };
    }
}
