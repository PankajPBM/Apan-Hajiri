import { spawn } from "node:child_process";

const args = process.argv.slice(2).filter((arg) => {
  const normalized = arg.replace(/\s+/g, "").toLowerCase();
  return normalized !== "--strictport";
});

const child = spawn("next", ["dev", ...args], {
  stdio: "inherit",
  shell: true,
});

child.on("exit", (code, signal) => {
  if (signal) {
    process.kill(process.pid, signal);
    return;
  }

  process.exit(code ?? 0);
});
