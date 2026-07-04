type ModuleShellProps = {
  title: string;
  description: string;
  children?: React.ReactNode;
};

export function ModuleShell({ title, description, children }: ModuleShellProps) {
  return (
    <div className="space-y-4">
      <div className="page-hero">
        <h1 className="text-2xl font-semibold">{title}</h1>
        <p className="text-sm">{description}</p>
      </div>
      <div className="surface p-5 shadow-sm">
        {children ?? <p className="text-sm text-[--muted-foreground]">UI module placeholder ready for Phase 2 implementation.</p>}
      </div>
    </div>
  );
}
