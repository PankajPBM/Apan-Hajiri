const actions = [
  "Add Employee",
  "Sync Devices",
  "Generate Report",
  "Download Attendance",
  "Leaves & Holiday",
  "Add Shift",
];

export function QuickActions() {
  return (
    <article className="surface p-4 shadow-sm">
      <h3 className="text-sm font-semibold">Quick Actions</h3>
      <div className="mt-4 grid grid-cols-2 gap-3">
        {actions.map((action) => (
          <button
            key={action}
            type="button"
            className="rounded-xl border border-[--border] bg-[--card] px-3 py-2 text-left text-sm font-medium hover:bg-[--accent]"
          >
            {action}
          </button>
        ))}
      </div>
    </article>
  );
}
