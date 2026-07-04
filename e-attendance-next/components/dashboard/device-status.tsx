import { devices } from "@/components/data/mock-data";

export function DeviceStatus() {
  return (
    <article className="surface p-4 shadow-sm">
      <h3 className="text-sm font-semibold">Device Status</h3>
      <div className="mt-4 space-y-2">
        {devices.map((device) => (
          <div key={device.id} className="flex items-center justify-between rounded-lg border border-[--border] bg-[--muted] px-3 py-2">
            <div>
              <p className="text-sm font-medium">{device.name}</p>
              <p className="text-xs text-[--muted-foreground]">{device.serial} - {device.location}</p>
            </div>
            <div className="text-right">
              <span className={`inline-flex rounded-full px-2 py-0.5 text-xs ${device.status === "online" ? "badge-online" : device.status === "delayed" ? "badge-pending" : "badge-offline"}`}>
                {device.status}
              </span>
              <p className="mt-1 text-xs text-[--muted-foreground]">Heartbeat: {device.lastHeartbeat}</p>
            </div>
          </div>
        ))}
      </div>
    </article>
  );
}
