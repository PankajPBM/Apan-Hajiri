"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Building2, CalendarDays, CheckSquare, Clock, Edit3, Filter, Info, Plus, RotateCcw, Search, Shield, Trash2, Users, X } from "lucide-react";
import { resolveApiBases } from "@/lib/api-bases";
import { fetchLicenseStatus } from "@/components/data/license-client";
import {
  ACCESS_CONTROL_UPDATED_EVENT, WEEKDAY_LABELS,
  type AccessControlCombination, type AccessControlDayRule, type AccessControlGroup,
  type AccessControlPrivilege, type AccessControlSnapshot, type AccessControlTimePeriod,
  type AccessControlVerificationMode, labelForVerificationMode,
  loadAccessControlFromApi, readAccessControlSnapshot, saveAccessControlToApi,
  ensureAccessControlDefaults,
} from "@/components/data/access-control-store";

type AccessSection = "time-period" | "groups" | "combination" | "privilege" | "duplicate-punch";
type DepartmentRow = { id: string; name: string; code: string; parentId: string | null; areaName?: string; areaCode?: string };
type DeviceRow = { serialNumber: string; name: string; area: string; department: string; status: string };
type EmployeeRow = { id: string; employeeCode: string; fullName: string; area: string; department: string; isActive?: boolean; groupId?: number; verifyMode?: number; timezoneProfile?: string };

// Per-employee privilege edit form
type EmpPrivForm = { empId: string; empCode: string; empName: string; groupId: string; verifyMode: string; timePeriodMode: "group"|"manual"; timePeriodIds: [string,string,string] };

type TimePeriodForm = { id: string | null; area: string; department: string; deviceSerials: string[]; periodNo: string; name: string; remark: string; days: AccessControlDayRule[] };
type GroupForm = { id: string | null; area: string; department: string; deviceSerials: string[]; groupNo: string; name: string; verificationMode: AccessControlVerificationMode; timePeriodIds: [string, string, string]; includeHoliday: boolean; remark: string };
type CombinationForm = { id: string | null; area: string; department: string; deviceSerials: string[]; combinationNo: string; name: string; groupIds: [string, string, string, string, string]; remark: string };
type PrivilegeForm = { id: string | null; area: string; department: string; deviceSerials: string[]; employeeIds: string[]; employeeNames: string[]; timePeriodMode: "group" | "manual"; groupId: string; timePeriodIds: [string, string, string]; verificationMode: "group" | "manual"; verificationModeValue: AccessControlVerificationMode; remark: string };
type ModalState = { kind: "time-period"; id: string | null } | { kind: "group"; id: string | null } | { kind: "combination"; id: string | null } | { kind: "privilege"; id: string | null } | { kind: "emp-privilege"; empId: string } | null;

const SECTION_TABS: Array<{ key: AccessSection; label: string; icon: React.ComponentType<{ className?: string }> }> = [
  { key: "time-period", label: "Time Period", icon: CalendarDays },
  { key: "groups", label: "Access Group", icon: Users },
  { key: "combination", label: "Combination", icon: CheckSquare },
  { key: "privilege", label: "Privilege", icon: Shield },
  { key: "duplicate-punch", label: "Device Duplicate Punch", icon: Clock },
];

const VERIFY_OPTS: Array<{ value: AccessControlVerificationMode; label: string }> = [
  { value: "0", label: "Auto identify" }, { value: "-1", label: "Inherit from group" },
  { value: "1", label: "Fingerprint only" }, { value: "2", label: "User ID only (PIN)" },
  { value: "3", label: "Password only" }, { value: "4", label: "Card only" },
  { value: "5", label: "Fingerprint OR password" }, { value: "6", label: "Fingerprint OR card" },
  { value: "7", label: "Card OR password" }, { value: "8", label: "User ID + fingerprint" },
  { value: "9", label: "Fingerprint + password" }, { value: "10", label: "Card + fingerprint" },
  { value: "11", label: "Card + password" }, { value: "12", label: "FP + password + card" },
  { value: "13", label: "User ID + FP + password" }, { value: "14", label: "(User ID+FP) OR (Card+FP)" },
  { value: "15", label: "Face only" }, { value: "16", label: "Face + fingerprint" },
  { value: "17", label: "Face + password" }, { value: "18", label: "Face + card" },
  { value: "19", label: "Face + FP + card" }, { value: "20", label: "Face + FP + password" },
  { value: "21", label: "Finger vein only" }, { value: "22", label: "Finger vein + password" },
  { value: "23", label: "Finger vein + card" }, { value: "24", label: "Finger vein + pw + card" },
  { value: "25", label: "Palm print only" }, { value: "26", label: "Palm print + card" },
  { value: "27", label: "Palm print + face" }, { value: "28", label: "Palm print + FP" },
  { value: "29", label: "Palm print + FP + face" },
];

const ic = "w-full rounded-lg border border-[#c8ddf0] bg-white px-3 py-2 text-sm outline-none focus:border-[#4a90c4] focus:ring-1 focus:ring-[#4a90c4]";

// ── helpers ──────────────────────────────────────────────────────────────────
function createId(p: string) { return `${p}-${Date.now()}-${Math.random().toString(36).slice(2,8)}`; }
function createDays(): AccessControlDayRule[] { return WEEKDAY_LABELS.map((_,i)=>({dayOfWeek:i,startTime:"08:30:00",endTime:"18:00:00",isActive:i!==0})); }
function compact<N extends number>(vals: string[], n: N): string[] { return Array.from({length:n},(_,i)=>vals[i]?.trim()??""); }
function nextSlot(rows: Array<{periodNo?:string;groupNo?:string;combinationNo?:string;area?:string;department?:string}>, key: "periodNo"|"groupNo"|"combinationNo", cur: string|null, area: string, dept: string) {
  if(cur){ const v=rows.find(r=>r[key]===cur)?.[key]; if(v) return v; }
  const used=new Set(rows.filter(r=>(r.area??"General")===area && (r.department??"All")===dept).map(r=>Number(r[key])).filter(n=>Number.isInteger(n)&&n>0));
  let s=1; while(used.has(s)) s++; return String(s);
}
function readStr(src: Record<string,unknown>, keys: string[]) { for(const k of keys){ const v=src[k]; if(typeof v==="string"&&v.trim()) return v.trim(); } return ""; }
function unwrap(p: unknown): unknown[] { if(Array.isArray(p)) return p; if(p&&typeof p==="object"){ const s=p as Record<string,unknown>; if(Array.isArray(s.items)) return s.items; if(Array.isArray(s.data)) return s.data; } return []; }
function tpLabel(tps: AccessControlTimePeriod[], id: string) { if(!id) return "—"; const t=tps.find(x=>x.id===id); return t?`${t.periodNo} - ${t.name}`:"—"; }
function grpLabel(gs: AccessControlGroup[], id: string) { if(!id) return "—"; const g=gs.find(x=>x.id===id); return g?`${g.groupNo} - ${g.name}`:"—"; }
function defaultGrpId(gs: AccessControlGroup[]) { return gs.find(g=>g.groupNo==="1")?.id??gs[0]?.id??""; }
function defaultTpId(tps: AccessControlTimePeriod[]) { return tps.find(t=>t.periodNo==="1")?.id??tps[0]?.id??""; }

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  let last: Error|null=null;
  for(const base of resolveApiBases({includeCurrentOrigin:true})){
    try{ const r=await fetch(`${base}${path}`,{cache:"no-store",...init}); if(r.ok) return await r.json() as T; last=new Error(`${r.status}`); }
    catch(e){ last=e instanceof Error?e:new Error("fetch failed"); }
  }
  throw last??new Error("unreachable");
}

function asDepts(p: unknown): DepartmentRow[] {
  const rows: Array<DepartmentRow | null> = unwrap(p).map((item,i)=>{ if(!item||typeof item!=="object") return null; const s=item as Record<string,unknown>; const name=readStr(s,["name","departmentName"]); if(!name) return null; return {id:readStr(s,["id"])||`d${i}`,code:readStr(s,["code","Code"])||`${i+1}`,name,parentId:null,areaName:readStr(s,["areaName","area"])||undefined,areaCode:readStr(s,["areaCode","AreaCode"])||undefined}; });
  return rows.filter((x): x is DepartmentRow=>x!==null);
}
function asDevices(p: unknown): DeviceRow[] {
  return unwrap(p).map(item=>{ const s=item&&typeof item==="object"?item as Record<string,unknown>:{}; return {serialNumber:readStr(s,["serialNumber","SerialNumber"]),name:readStr(s,["name","Name"]),area:readStr(s,["area","Area"])||"Default Area",department:readStr(s,["department","Department"])||"Default Department",status:readStr(s,["status","Status"])||"offline"}; });
}
function asEmps(p: unknown): EmployeeRow[] {
  const rows: Array<EmployeeRow | null> = unwrap(p).map((item,i)=>{
    if(!item||typeof item!=="object") return null;
    const s=item as Record<string,unknown>;
    const fullName=readStr(s,["fullName","name"]);
    if(!fullName) return null;
    const dept=readStr(s,["department","departmentName"])||(Array.isArray(s.departments)?String(s.departments[0]??""):"");
    const area=readStr(s,["area","areaName"])||(Array.isArray(s.areas)?String(s.areas[0]??""):"");
    const isActive = s.isActive !== undefined ? Boolean(s.isActive) : s.IsActive !== undefined ? Boolean(s.IsActive) : true;
    const rawGrp = typeof s.groupId === "number" ? s.groupId : undefined;
    const effectiveGrp = !isActive ? 0 : (rawGrp ?? 1);
    return {
      id:readStr(s,["id"])||`e${i}`,
      employeeCode:readStr(s,["employeeCode","code"])||`EMP${i}`,
      fullName,
      area:area||"Default Area",
      department:dept||"Default Department",
      isActive,
      groupId: effectiveGrp,
      verifyMode:typeof s.verifyMode==="number"?s.verifyMode:undefined,
      timezoneProfile:typeof s.timezoneProfile==="string"?s.timezoneProfile:undefined
    };
  });
  return rows.filter((x): x is EmployeeRow=>x!==null);
}

// ── component ─────────────────────────────────────────────────────────────────
export default function AccessControlPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const section = (searchParams.get("section") as AccessSection|null)??"time-period";
  // NOTE: `sel` must be declared here (before any useEffect that lists it in a dependency array).
  // A hook's dependency array is evaluated during render top-to-bottom, so referencing `sel` in an
  // effect above its declaration throws "Cannot access 'sel' before initialization" (TDZ crash).
  const sel = useMemo<AccessSection>(()=>["groups","combination","privilege","duplicate-punch"].includes(section)?section as AccessSection:"time-period",[section]);

  const [snapshot, setSnapshot] = useState<AccessControlSnapshot>(()=>readAccessControlSnapshot());
  const [depts, setDepts] = useState<DepartmentRow[]>([]);
  const [areas, setAreas] = useState<Array<{code:string;name:string}>>([]);
  const [emps, setEmps] = useState<EmployeeRow[]>([]);
  const [devs, setDevs] = useState<DeviceRow[]>([]);
  const [areaFilter, setAreaFilter] = useState("");
  const [deptFilter, setDeptFilter] = useState("");
  const [search, setSearch] = useState("");
  const [msg, setMsg] = useState<string|null>(null);
  const [err, setErr] = useState<string|null>(null);
  const [modal, setModal] = useState<ModalState>(null);
  const [tpForm, setTpForm] = useState<TimePeriodForm>(()=>({id:null,area:"",department:"",deviceSerials:[],periodNo:"",name:"",remark:"",days:createDays()}));
  const [grpForm, setGrpForm] = useState<GroupForm>(()=>({id:null,area:"",department:"",deviceSerials:[],groupNo:"",name:"",verificationMode:"0",timePeriodIds:["","",""],includeHoliday:false,remark:""}));
  const [combForm, setCombForm] = useState<CombinationForm>(()=>({id:null,area:"",department:"",deviceSerials:[],combinationNo:"",name:"",groupIds:["","","","",""],remark:""}));
  const [privForm, setPrivForm] = useState<PrivilegeForm>(()=>({id:null,area:"",department:"",deviceSerials:[],employeeIds:[],employeeNames:[],timePeriodMode:"group",groupId:"",timePeriodIds:["","",""],verificationMode:"group",verificationModeValue:"0",remark:""}));
  const [empPrivForm, setEmpPrivForm] = useState<EmpPrivForm>({empId:"",empCode:"",empName:"",groupId:"1",verifyMode:"-1",timePeriodMode:"group",timePeriodIds:["","",""]});
  const [empPrivSaving, setEmpPrivSaving] = useState(false);
  const [loadingLicense, setLoadingLicense] = useState(true);
  const [isLocked, setIsLocked] = useState(false);

  const [dupPeriodMinutes, setDupPeriodMinutes] = useState<number>(() => snapshot.deviceDuplicatePunchPeriodMinutes ?? 3);
  const [targetSerials, setTargetSerials] = useState<string[]>(() => snapshot.deviceSerials ?? []);
  const [dupDevSearch, setDupDevSearch] = useState("");
  const [showDupInfo, setShowDupInfo] = useState(false);
  const [savingDupPeriod, setSavingDupPeriod] = useState(false);

  useEffect(() => {
    if (typeof snapshot.deviceDuplicatePunchPeriodMinutes === "number") {
      setDupPeriodMinutes(snapshot.deviceDuplicatePunchPeriodMinutes);
    }
    if (Array.isArray(snapshot.deviceSerials)) {
      setTargetSerials(snapshot.deviceSerials);
    }
  }, [snapshot.deviceDuplicatePunchPeriodMinutes, snapshot.deviceSerials]);

  const filteredDevices = useMemo(() => {
    return devs.filter((d) => {
      if (areaFilter !== "All" && d.area !== areaFilter) return false;
      if (dupDevSearch.trim()) {
        const q = dupDevSearch.toLowerCase();
        return d.serialNumber.toLowerCase().includes(q) || d.name.toLowerCase().includes(q);
      }
      return true;
    });
  }, [devs, areaFilter, dupDevSearch]);

  const saveDupPeriod = useCallback(async () => {
    setSavingDupPeriod(true);
    setErr(null);
    setMsg(null);
    try {
      const val = Math.max(0, Math.min(99, Number(dupPeriodMinutes) || 0));
      const updated = {
        ...snapshot,
        deviceDuplicatePunchPeriodMinutes: val,
        deviceSerials: targetSerials,
      };
      await saveAccessControlToApi(updated);
      setSnapshot(updated);
      setMsg(`Device Duplicate Punch Period saved as ${val} minute(s). Synchronization commands (SET OPTION AlarmReRec=${val} & ReCheckMin=${val}) queued for devices.`);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Failed to save Device Duplicate Punch Period.");
    } finally {
      setSavingDupPeriod(false);
    }
  }, [dupPeriodMinutes, targetSerials, snapshot]);

  const [selectedEmpIds, setSelectedEmpIds] = useState<string[]>([]);
  const [batchModalOpen, setBatchModalOpen] = useState(false);
  const [batchSaving, setBatchSaving] = useState(false);
  const [batchForm, setBatchForm] = useState<{
    groupId: string;
    verifyMode: string;
    timePeriodMode: "group" | "manual";
    timePeriodIds: [string, string, string];
  }>({
    groupId: "1",
    verifyMode: "-1",
    timePeriodMode: "group",
    timePeriodIds: ["", "", ""],
  });

  const [sortField, setSortField] = useState<string>("periodNo");
  const [sortAsc, setSortAsc] = useState<boolean>(true);

  const handleSort = (field: string) => {
    if (sortField === field) {
      setSortAsc((prev) => !prev);
    } else {
      setSortField(field);
      setSortAsc(true);
    }
  };

  useEffect(() => {
    setSelectedEmpIds([]);
    if (sel === "time-period") {
      setSortField("periodNo");
      setSortAsc(true);
    } else if (sel === "groups") {
      setSortField("groupNo");
      setSortAsc(true);
    } else if (sel === "combination") {
      setSortField("combinationNo");
      setSortAsc(true);
    } else if (sel === "privilege") {
      setSortField("employeeCode");
      setSortAsc(true);
    }
  }, [areaFilter, search, section, sel]);

  useEffect(() => {
    let active = true;
    fetchLicenseStatus()
      .then((status) => {
        if (active) {
          setIsLocked(!status.isValid);
          setLoadingLicense(false);
        }
      })
      .catch((err) => {
        console.error("Failed to fetch license status", err);
        if (active) {
          setIsLocked(true);
          setLoadingLicense(false);
        }
      });
    return () => { active = false; };
  }, []);


  // ── effects ──────────────────────────────────────────────────────────────
  useEffect(()=>{
    const refresh=()=>setSnapshot(readAccessControlSnapshot());
    // Always load from API first - don't rely on stale localStorage
    void loadAccessControlFromApi().then(setSnapshot).catch(()=>{ refresh(); });
    window.addEventListener(ACCESS_CONTROL_UPDATED_EVENT,refresh);
    window.addEventListener("storage",refresh);
    return ()=>{ window.removeEventListener(ACCESS_CONTROL_UPDATED_EVENT,refresh); window.removeEventListener("storage",refresh); };
  },[]);

  const ensureAreaDefaultsUI = useCallback(async (areas: {name: string, code:string}[], devices: DeviceRow[], currentSnapshot: AccessControlSnapshot) => {
    try {
      const saved = await ensureAccessControlDefaults(areas, devices.map(d=>({serialNumber:d.serialNumber,area:d.area})), currentSnapshot);
      setSnapshot(saved);
    } catch (e) {
      console.error("Failed to seed area defaults", e);
    }
  }, []);

  useEffect(()=>{
    void (async()=>{
      try {
        const [setup, ep, dvp] = await Promise.allSettled([
          apiFetch<any>("/api/personnel/setup"),
          apiFetch<unknown>("/api/personnel/employees"),
          apiFetch<unknown>("/api/devices")
        ]);

        let currentAreas: {name:string;code:string}[] = [];
        let currentDepts: DepartmentRow[] = [];

        if (setup.status === "fulfilled") {
          const s = setup.value;
          currentAreas = unwrap(s.areas).map(x => {
            const row = x && typeof x === "object" ? x as Record<string, unknown> : {};
            return {
              code: readStr(row, ["code", "Code"]) || "",
              name: readStr(row, ["name", "Name"]) || ""
            };
          }).filter(a => a.name);
          setAreas(currentAreas);
          currentDepts = asDepts(s.departments);
          setDepts(currentDepts);

          // Default the area filter to the first area (access control is area-scoped only).
          if (currentAreas.length > 0) {
            setAreaFilter(currentAreas[0].name);
          }
        }

        if (ep.status === "fulfilled") setEmps(asEmps(ep.value));
        let currentDevices: DeviceRow[] = [];
        if (dvp.status === "fulfilled") { currentDevices = asDevices(dvp.value); setDevs(currentDevices); }

        // Seed per-area defaults AFTER devices are known, so the defaults carry the area's
        // device serials and sync to them.
        if (currentAreas.length > 0) {
          void ensureAreaDefaultsUI(currentAreas, currentDevices, readAccessControlSnapshot());
        }
      } catch (e) {
        console.error("Failed to load setup data", e);
      }
    })();
  }, [ensureAreaDefaultsUI]);

  // Access control is area-scoped only; department filtering is intentionally disabled
  // (deptFilter stays empty so all list filters match by area alone).

  // ── memos ─────────────────────────────────────────────────────────────────
  const areaOpts = useMemo(()=>{
    if(areas.length>0) return areas.map(a=>a.name);
    const s=new Set(depts.map(d=>d.areaName).filter(Boolean) as string[]);
    return Array.from(s).sort();
  },[areas,depts]);

  const firstArea = useMemo(()=>areas[0]?.name??depts[0]?.areaName??"General",[areas,depts]);

  const deptOpts = useMemo(()=>{
    if(!areaFilter) return Array.from(new Set(depts.map(d=>d.name))).sort();
    const names=depts.filter(d=>d.areaName===areaFilter).map(d=>d.name);
    return Array.from(new Set(names)).sort();
  },[areaFilter,depts]);

  const privDeptOpts = useMemo(()=>{
    if(!privForm.area) return Array.from(new Set(depts.map(d=>d.name))).sort();
    const names=depts.filter(d=>d.areaName===privForm.area).map(d=>d.name);
    return Array.from(new Set(names)).sort();
  },[depts,privForm.area]);

  const deptAreaMap = useMemo(()=>new Map(depts.map(d=>[d.name,d.areaName??""])),[depts]);

  const privEmps = useMemo(()=>emps.filter(e=>{
    const aOk=!privForm.area||e.area===privForm.area||deptAreaMap.get(e.department)===privForm.area;
    const dOk=!deptFilter||e.department===deptFilter;
    return aOk&&dOk;
  }),[emps,privForm.area,deptFilter,deptAreaMap]);

  const selPrivGrp = useMemo(()=>snapshot.groups.find(g=>g.id===privForm.groupId)??null,[privForm.groupId,snapshot.groups]);

  const filt = useMemo(()=>{
    const q=search.toLowerCase();
    const aOk = (itemArea: string) => {
      if (!areaFilter) return true;
      const target = areaFilter.trim().toLowerCase();
      const actual = (itemArea || "").trim().toLowerCase();
      if (actual === target) return true;
      const matchedArea = areas.find(a => a.name.toLowerCase() === target || a.code.toLowerCase() === target);
      if (matchedArea) {
        return actual === matchedArea.name.toLowerCase() || actual === matchedArea.code.toLowerCase();
      }
      return false;
    };
    return {
      tp: snapshot.timePeriods.filter(x=>aOk(x.area)&&`${x.periodNo} ${x.name} ${x.area}`.toLowerCase().includes(q)),
      grp: snapshot.groups.filter(x=>aOk(x.area)&&`${x.groupNo} ${x.name} ${x.area}`.toLowerCase().includes(q)),
      comb: snapshot.combinations.filter(x=>aOk(x.area)&&`${x.combinationNo} ${x.name} ${x.area}`.toLowerCase().includes(q)),
      priv: snapshot.privileges.filter(x=>aOk(x.area)&&`${x.employeeNames.join(" ")} ${x.area}`.toLowerCase().includes(q)),
    };
  },[snapshot,areaFilter,search]);

  const sortedTp = useMemo(() => {
    const data = [...filt.tp];
    if (sortField && sel === "time-period") {
      data.sort((a, b) => {
        let result = 0;
        if (sortField === "periodNo") {
          result = parseInt(a.periodNo || "0", 10) - parseInt(b.periodNo || "0", 10);
        } else if (sortField === "name") {
          result = (a.name || "").toLowerCase().localeCompare((b.name || "").toLowerCase());
        } else if (sortField === "area") {
          result = (a.area || "").toLowerCase().localeCompare((b.area || "").toLowerCase());
        } else if (sortField === "activeDays") {
          result = a.days.filter(d => d.isActive).length - b.days.filter(d => d.isActive).length;
        } else if (sortField === "devices") {
          result = a.deviceSerials.length - b.deviceSerials.length;
        }
        return sortAsc ? result : -result;
      });
    }
    return data;
  }, [filt.tp, sortField, sortAsc, sel]);

  const sortedGrp = useMemo(() => {
    const data = [...filt.grp];
    if (sortField && sel === "groups") {
      data.sort((a, b) => {
        let result = 0;
        if (sortField === "groupNo") {
          result = parseInt(a.groupNo || "0", 10) - parseInt(b.groupNo || "0", 10);
        } else if (sortField === "name") {
          result = (a.name || "").toLowerCase().localeCompare((b.name || "").toLowerCase());
        } else if (sortField === "area") {
          result = (a.area || "").toLowerCase().localeCompare((b.area || "").toLowerCase());
        } else if (sortField === "timePeriod1") {
          result = (a.timePeriodIds[0] || "").localeCompare(b.timePeriodIds[0] || "");
        } else if (sortField === "timePeriod2") {
          result = (a.timePeriodIds[1] || "").localeCompare(b.timePeriodIds[1] || "");
        } else if (sortField === "timePeriod3") {
          result = (a.timePeriodIds[2] || "").localeCompare(b.timePeriodIds[2] || "");
        } else if (sortField === "verification") {
          result = (a.verificationMode || "").localeCompare(b.verificationMode || "");
        } else if (sortField === "holiday") {
          result = (a.includeHoliday ? 1 : 0) - (b.includeHoliday ? 1 : 0);
        }
        return sortAsc ? result : -result;
      });
    }
    return data;
  }, [filt.grp, sortField, sortAsc, sel]);

  const sortedComb = useMemo(() => {
    const data = [...filt.comb];
    if (sortField && sel === "combination") {
      data.sort((a, b) => {
        let result = 0;
        if (sortField === "combinationNo") {
          result = parseInt(a.combinationNo || "0", 10) - parseInt(b.combinationNo || "0", 10);
        } else if (sortField === "name") {
          result = (a.name || "").toLowerCase().localeCompare((b.name || "").toLowerCase());
        } else if (sortField === "area") {
          result = (a.area || "").toLowerCase().localeCompare((b.area || "").toLowerCase());
        }
        return sortAsc ? result : -result;
      });
    }
    return data;
  }, [filt.comb, sortField, sortAsc, sel]);

  const sortedFilteredEmps = useMemo(() => {
    const filteredEmps = emps.filter(e=>{
      const aOk=e.area===areaFilter||deptAreaMap.get(e.department)===areaFilter;
      const q=search.toLowerCase();
      return aOk&&(`${e.employeeCode} ${e.fullName}`.toLowerCase().includes(q));
    });

    if (sel !== "privilege" || !sortField) return filteredEmps;

    const data = [...filteredEmps];
    data.sort((a, b) => {
      let valA: any = "";
      let valB: any = "";

      if (sortField === "employeeCode") {
        const numA = parseInt(a.employeeCode, 10);
        const numB = parseInt(b.employeeCode, 10);
        if (!isNaN(numA) && !isNaN(numB)) {
          return sortAsc ? numA - numB : numB - numA;
        }
        valA = a.employeeCode;
        valB = b.employeeCode;
      } else if (sortField === "firstName") {
        valA = (a.fullName || "").trim().split(/\s+/)[0] || "";
        valB = (b.fullName || "").trim().split(/\s+/)[0] || "";
      } else if (sortField === "lastName") {
        valA = (a.fullName || "").trim().split(/\s+/).slice(1).join(" ") || "";
        valB = (b.fullName || "").trim().split(/\s+/).slice(1).join(" ") || "";
      } else if (sortField === "groupId") {
        valA = !a.isActive ? 0 : (a.groupId ?? 1);
        valB = !b.isActive ? 0 : (b.groupId ?? 1);
        return sortAsc ? valA - valB : valB - valA;
      } else if (sortField === "verifyMode") {
        valA = a.verifyMode ?? -1;
        valB = b.verifyMode ?? -1;
        return sortAsc ? valA - valB : valB - valA;
      } else if (sortField === "timezoneProfile") {
        const tzEsc = (x: any) => {
          const tz = x.timezoneProfile ?? "0000000000000000";
          return tz !== "0000000000000000" && tz.slice(4,8) !== "0000" ? "Personnel" : "Group";
        };
        valA = tzEsc(a);
        valB = tzEsc(b);
      } else if (sortField.startsWith("slot")) {
        const slotIndex = parseInt(sortField.replace("slot", ""), 10);
        const tzSlot = (x: any) => {
          const tz = x.timezoneProfile ?? "0000000000000000";
          const isPersonalTp = tz !== "0000000000000000" && tz.slice(4,8) !== "0000";
          if (!isPersonalTp) return 0;
          if (slotIndex === 1) return parseInt(tz.slice(4,8),10)||0;
          if (slotIndex === 2) return parseInt(tz.slice(8,12),10)||0;
          return parseInt(tz.slice(12,16),10)||0;
        };
        valA = tzSlot(a);
        valB = tzSlot(b);
        return sortAsc ? valA - valB : valB - valA;
      }

      const strA = String(valA).toLowerCase();
      const strB = String(valB).toLowerCase();
      return sortAsc ? strA.localeCompare(strB) : strB.localeCompare(strA);
    });
    return data;
  }, [emps, areaFilter, deptAreaMap, search, sel, sortField, sortAsc]);

  const counts = { tp:snapshot.timePeriods.length, grp:snapshot.groups.length, comb:snapshot.combinations.length, priv:snapshot.privileges.length };

  // ── open handlers ─────────────────────────────────────────────────────────
  const openTp = useCallback((item?: AccessControlTimePeriod|null)=>{
    setTpForm(item?{id:item.id,area:item.area,department:item.department,deviceSerials:[...item.deviceSerials],periodNo:item.periodNo,name:item.name,remark:item.remark,days:item.days.map(d=>({...d}))}:{id:null,area:areaFilter||firstArea,department:deptFilter||"All",deviceSerials:[],periodNo:"",name:"",remark:"",days:createDays()});
    setModal({kind:"time-period",id:item?.id??null});
  },[areaFilter,deptFilter,firstArea]);

  const openGrp = useCallback((item?: AccessControlGroup|null)=>{
    setGrpForm(item?{id:item.id,area:item.area,department:item.department,deviceSerials:[...item.deviceSerials],groupNo:item.groupNo,name:item.name,verificationMode:item.verificationMode,timePeriodIds:[...item.timePeriodIds] as [string,string,string],includeHoliday:item.includeHoliday,remark:item.remark}:{id:null,area:areaFilter||firstArea,department:deptFilter||"All",deviceSerials:[],groupNo:"",name:"",verificationMode:"0",timePeriodIds:[defaultTpId(snapshot.timePeriods),"",""],includeHoliday:false,remark:""});
    setModal({kind:"group",id:item?.id??null});
  },[areaFilter,deptFilter,firstArea,snapshot.timePeriods]);

  const openComb = useCallback((item?: AccessControlCombination|null)=>{
    setCombForm(item?{id:item.id,area:item.area,department:item.department,deviceSerials:[...item.deviceSerials],combinationNo:item.combinationNo,name:item.name,groupIds:[...item.groupIds] as [string,string,string,string,string],remark:item.remark}:{id:null,area:areaFilter||firstArea,department:deptFilter||"All",deviceSerials:[],combinationNo:"",name:"",groupIds:["","","","",""],remark:""});
    setModal({kind:"combination",id:item?.id??null});
  },[areaFilter,deptFilter,firstArea]);

  const openPriv = useCallback((item?: AccessControlPrivilege|null)=>{
    const useGrpVer=item?.verificationMode==="-1"||item?.verificationMode==="group";
    setPrivForm(item?{id:item.id,area:item.area,department:item.department,deviceSerials:[...item.deviceSerials],employeeIds:[...item.employeeIds],employeeNames:[...item.employeeNames],timePeriodMode:item.groupId?"group":"manual",groupId:item.groupId,verificationMode:useGrpVer?"group":"manual",verificationModeValue:useGrpVer?"0":item.verificationMode,timePeriodIds:[...item.timePeriodIds] as [string,string,string],remark:item.remark}:{id:null,area:areaFilter||firstArea,department:deptFilter||"All",deviceSerials:[],employeeIds:[],employeeNames:[],timePeriodMode:"group",groupId:defaultGrpId(snapshot.groups),timePeriodIds:[defaultTpId(snapshot.timePeriods),"",""],verificationMode:"group",verificationModeValue:"0",remark:""});
    setModal({kind:"privilege",id:item?.id??null});
  },[areaFilter,deptFilter,firstArea,snapshot.groups,snapshot.timePeriods]);

  const closeModal = useCallback(()=>setModal(null),[]);

  const openEmpPriv = useCallback((emp: EmployeeRow)=>{
    const effectiveGrp = !emp.isActive ? 0 : (emp.groupId ?? 1);
    const grp = snapshot.groups.find(g=>Number(g.groupNo)===effectiveGrp);
    const tpMode = emp.timezoneProfile && emp.timezoneProfile !== "0000000000000000" ? "manual" : "group";
    // Parse personal time period slots from timezoneProfile (digits 5-8, 9-12, 13-16)
    const tz = emp.timezoneProfile ?? "0000000000000000";
    const slot1 = parseInt(tz.slice(4,8),10)||0;
    const slot2 = parseInt(tz.slice(8,12),10)||0;
    const slot3 = parseInt(tz.slice(12,16),10)||0;
    const tpByNo = (no: number) => snapshot.timePeriods.find(t=>Number(t.periodNo)===no)?.id??"";
    setEmpPrivForm({
      empId: emp.id,
      empCode: emp.employeeCode,
      empName: emp.fullName,
      groupId: String(effectiveGrp),
      verifyMode: String(emp.verifyMode??-1),
      timePeriodMode: tpMode,
      timePeriodIds: [tpByNo(slot1), tpByNo(slot2), tpByNo(slot3)],
    });
    setModal({kind:"emp-privilege",empId:emp.id});
  },[snapshot.groups,snapshot.timePeriods]);

  const saveEmpPriv = useCallback(async()=>{
    setEmpPrivSaving(true); setMsg(null); setErr(null);
    try {
      const grpNo = Number.isFinite(Number(empPrivForm.groupId)) ? Number(empPrivForm.groupId) : 1;
      // Build timezoneProfile: 0000 + tp1(4) + tp2(4) + tp3(4) + 0000
      let tz = "0000000000000000";
      if(empPrivForm.timePeriodMode==="manual"){
        const slot=(id:string)=>{ const t=snapshot.timePeriods.find(x=>x.id===id); return t?Number(t.periodNo):0; };
        const s1=slot(empPrivForm.timePeriodIds[0]), s2=slot(empPrivForm.timePeriodIds[1]), s3=slot(empPrivForm.timePeriodIds[2]);
        tz=`0000${String(s1).padStart(4,"0")}${String(s2).padStart(4,"0")}${String(s3).padStart(4,"0")}`;
      }
      // Save to employee via PUT (which syncs to device)
      const emp = emps.find(e=>e.id===empPrivForm.empId);
      if(!emp) throw new Error("Employee not found");
      await apiFetch<unknown>(`/api/personnel/employees/${empPrivForm.empId}`, {
        method:"PUT",
        headers:{"Content-Type":"application/json"},
        body:JSON.stringify({
          employeeCode: emp.employeeCode,
          fullName: emp.fullName,
          department: emp.department,
          departmentCodes: [],
          position: "",
          isActive: grpNo === 0 ? false : (emp.isActive ?? true),
          privilege: "User",
          verifyMode: Number(empPrivForm.verifyMode),
          groupId: grpNo,
          cardNumber: "",
          pictureDataUrl: "",
          mobileNumber: "",
          gender: "Male",
          hireDate: "",
          password: "123456",
        }),
      });
      // Refresh employee list to show updated values
      const updated = await apiFetch<unknown>("/api/personnel/employees");
      setEmps(asEmps(updated));
      setMsg(`Privilege updated for ${emp.fullName} and synced to device.`);
      closeModal();
    } catch(e){ setErr(e instanceof Error?e.message:"Save failed"); }
    finally{ setEmpPrivSaving(false); }
  },[closeModal,empPrivForm,emps,snapshot.groups,snapshot.timePeriods]);

  const openBatchEditModal = useCallback(() => {
    setBatchForm({
      groupId: "1",
      verifyMode: "-1",
      timePeriodMode: "group",
      timePeriodIds: ["", "", ""],
    });
    setBatchModalOpen(true);
  }, []);

  const saveBatchEdit = useCallback(async () => {
    setBatchSaving(true);
    setMsg(null);
    setErr(null);
    try {
      const grpNo = Number.isFinite(Number(batchForm.groupId)) ? Number(batchForm.groupId) : 1;
      let tz = "0000000000000000";
      if (batchForm.timePeriodMode === "manual") {
        const slot = (id: string) => {
          const t = snapshot.timePeriods.find(x => x.id === id);
          return t ? Number(t.periodNo) : 0;
        };
        const s1 = slot(batchForm.timePeriodIds[0]);
        const s2 = slot(batchForm.timePeriodIds[1]);
        const s3 = slot(batchForm.timePeriodIds[2]);
        tz = `0000${String(s1).padStart(4, "0")}${String(s2).padStart(4, "0")}${String(s3).padStart(4, "0")}`;
      }

      await Promise.all(
        selectedEmpIds.map(async id => {
          const emp = emps.find(e => e.id === id);
          if (!emp) return;
          await apiFetch<unknown>(`/api/personnel/employees/${id}`, {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              employeeCode: emp.employeeCode,
              fullName: emp.fullName,
              department: emp.department,
              departmentCodes: [],
              position: "",
              isActive: grpNo === 0 ? false : (emp.isActive ?? true),
              privilege: "User",
              verifyMode: Number(batchForm.verifyMode),
              groupId: grpNo,
              timezoneProfile: tz,
              cardNumber: "",
              pictureDataUrl: "",
              mobileNumber: "",
              gender: "Male",
              hireDate: "",
              password: "123456",
            }),
          });
        })
      );

      const updated = await apiFetch<unknown>("/api/personnel/employees");
      setEmps(asEmps(updated));
      setSelectedEmpIds([]);
      setMsg(`Privileges updated for ${selectedEmpIds.length} employees and synced to devices.`);
      setBatchModalOpen(false);
    } catch (e) {
      setErr(e instanceof Error ? e.message : "Batch save failed");
    } finally {
      setBatchSaving(false);
    }
  }, [batchForm, selectedEmpIds, emps, snapshot.timePeriods]);

  // ── save handlers with replication ──────────────────────────────────────────
  const saveTp = useCallback(async()=>{
    setMsg(null); setErr(null);
    const area=tpForm.area;
    const resolvedPeriodNo=tpForm.periodNo||nextSlot(snapshot.timePeriods,"periodNo",tpForm.id,area,"All");
    // Area-only: one Time Period per (area, periodNo). Prevent duplicates within the same area.
    const existing = snapshot.timePeriods.find(x => x.area === area && x.periodNo === resolvedPeriodNo);
    if (existing && existing.id !== tpForm.id) { setErr(`Time Period ${resolvedPeriodNo} already exists in this area.`); return; }
    const next: AccessControlTimePeriod = {
      id: tpForm.id ?? existing?.id ?? createId("period"),
      area, department: "All", deviceSerials: tpForm.deviceSerials, periodNo: resolvedPeriodNo,
      name: tpForm.name||"Time Period", remark: tpForm.remark, days: tpForm.days,
      createdAt: existing?.createdAt ?? new Date().toISOString(), updatedAt: new Date().toISOString()
    };
    const nextTpList = (existing || tpForm.id)
      ? snapshot.timePeriods.map(x => x.id === next.id ? next : x)
      : [...snapshot.timePeriods, next];

    try { await saveAccessControlToApi({...snapshot, timePeriods: nextTpList}); setMsg("Time period saved for area."); closeModal(); }
    catch(e){ setErr(e instanceof Error?e.message:"Save failed"); }
  },[closeModal, snapshot, tpForm]);

  const saveGrp = useCallback(async()=>{
    setMsg(null); setErr(null);
    const area=grpForm.area;
    const resolvedGroupNo = grpForm.groupNo||nextSlot(snapshot.groups,"groupNo",grpForm.id,area,"All");
    const existing = snapshot.groups.find(x => x.area === area && x.groupNo === resolvedGroupNo);
    if (existing && existing.id !== grpForm.id) { setErr(`Access Group ${resolvedGroupNo} already exists in this area.`); return; }
    const next: AccessControlGroup = {
      id: grpForm.id ?? existing?.id ?? createId("group"),
      area, department: "All", deviceSerials: grpForm.deviceSerials, groupNo: resolvedGroupNo,
      name: grpForm.name||"Group", verificationMode: grpForm.verificationMode,
      timePeriodIds: compact(grpForm.timePeriodIds, 3) as [string,string,string],
      includeHoliday: grpForm.includeHoliday, remark: grpForm.remark,
      createdAt: existing?.createdAt ?? new Date().toISOString(), updatedAt: new Date().toISOString()
    };
    const nextGrpList = (existing || grpForm.id)
      ? snapshot.groups.map(x => x.id === next.id ? next : x)
      : [...snapshot.groups, next];

    try{ await saveAccessControlToApi({...snapshot, groups: nextGrpList}); setMsg("Access group saved for area."); closeModal(); }
    catch(e){ setErr(e instanceof Error?e.message:"Save failed"); }
  },[closeModal, grpForm, snapshot]);

  const saveComb = useCallback(async()=>{
    setMsg(null); setErr(null);
    const area=combForm.area;
    const resolvedCombNo=combForm.combinationNo||nextSlot(snapshot.combinations,"combinationNo",combForm.id,area,"All");
    const existing = snapshot.combinations.find(x => x.area === area && x.combinationNo === resolvedCombNo);
    if (existing && existing.id !== combForm.id) { setErr(`Combination ${resolvedCombNo} already exists in this area.`); return; }
    const next: AccessControlCombination = {
      id: combForm.id ?? existing?.id ?? createId("combination"),
      area, department: "All", deviceSerials: combForm.deviceSerials, combinationNo: resolvedCombNo,
      name: combForm.name||"Combination", groupIds: compact(combForm.groupIds, 5) as [string,string,string,string,string],
      remark: combForm.remark,
      createdAt: existing?.createdAt ?? new Date().toISOString(), updatedAt: new Date().toISOString()
    };
    const nextCombList = (existing || combForm.id)
      ? snapshot.combinations.map(x => x.id === next.id ? next : x)
      : [...snapshot.combinations, next];

    try{ await saveAccessControlToApi({...snapshot, combinations: nextCombList}); setMsg("Combination saved for area."); closeModal(); }
    catch(e){ setErr(e instanceof Error?e.message:"Save failed"); }
  },[closeModal, combForm, snapshot]);

  const savePriv = useCallback(async()=>{
    setMsg(null); setErr(null);
    const area=privForm.area;
    const dept=privForm.department;
    const effGrpId=privForm.groupId||defaultGrpId(snapshot.groups);
    const selGrp=snapshot.groups.find(x=>x.id===effGrpId)??null;
    const savedTpIds=privForm.timePeriodMode==="group"&&selGrp?selGrp.timePeriodIds:compact(privForm.timePeriodIds,3) as [string,string,string];
    const savedVer=privForm.verificationMode==="group"?"-1":privForm.verificationModeValue;
    const next: AccessControlPrivilege = {id:privForm.id??createId("privilege"),area,department:dept,deviceSerials:privForm.deviceSerials,employeeIds:privForm.employeeIds,employeeNames:privForm.employeeNames,groupId:effGrpId,verificationMode:savedVer,timePeriodIds:savedTpIds,remark:privForm.remark,createdAt:snapshot.privileges.find(x=>x.id===privForm.id)?.createdAt??new Date().toISOString(),updatedAt:new Date().toISOString()};
    const privileges=snapshot.privileges.some(x=>x.id===next.id)?snapshot.privileges.map(x=>x.id===next.id?next:x):[...snapshot.privileges,next];
    try{ await saveAccessControlToApi({...snapshot,privileges}); setMsg("Privilege saved."); closeModal(); }
    catch(e){ setErr(e instanceof Error?e.message:"Save failed"); }
  },[closeModal,privForm,snapshot]);

  // ── delete handlers ───────────────────────────────────────────────────────
  const delTp = useCallback(async(id:string)=>{
    setMsg(null); setErr(null);
    if(snapshot.timePeriods.find(x=>x.id===id)?.periodNo==="1"){ setErr("Cannot delete default Time Period 1."); return; }
    const timePeriods=snapshot.timePeriods.filter(x=>x.id!==id);
    const groups=snapshot.groups.map(g=>({...g,timePeriodIds:g.timePeriodIds.map(v=>v===id?"":v) as [string,string,string]}));
    const privileges=snapshot.privileges.map(p=>({...p,timePeriodIds:p.timePeriodIds.map(v=>v===id?"":v) as [string,string,string]}));
    try{ await saveAccessControlToApi({timePeriods,groups,combinations:snapshot.combinations,privileges}); setMsg("Deleted."); }
    catch(e){ setErr(e instanceof Error?e.message:"Delete failed"); }
  },[snapshot]);

  const delGrp = useCallback(async(id:string)=>{
    setMsg(null); setErr(null);
    if(snapshot.groups.find(x=>x.id===id)?.groupNo==="1"){ setErr("Cannot delete default Access Group 1."); return; }
    const groups=snapshot.groups.filter(x=>x.id!==id);
    const combinations=snapshot.combinations.map(c=>({...c,groupIds:c.groupIds.map(v=>v===id?"":v) as [string,string,string,string,string]}));
    const privileges=snapshot.privileges.map(p=>({...p,groupId:p.groupId===id?"":p.groupId}));
    try{ await saveAccessControlToApi({timePeriods:snapshot.timePeriods,groups,combinations,privileges}); setMsg("Deleted."); }
    catch(e){ setErr(e instanceof Error?e.message:"Delete failed"); }
  },[snapshot]);

  const delComb = useCallback(async(id:string)=>{
    setMsg(null); setErr(null);
    if(snapshot.combinations.find(x=>x.id===id)?.combinationNo==="1"){ setErr("Cannot delete default Combination 1."); return; }
    try{ await saveAccessControlToApi({...snapshot,combinations:snapshot.combinations.filter(x=>x.id!==id)}); setMsg("Deleted."); }
    catch(e){ setErr(e instanceof Error?e.message:"Delete failed"); }
  },[snapshot]);

  const delPriv = useCallback(async(id:string)=>{
    setMsg(null); setErr(null);
    try{ await saveAccessControlToApi({...snapshot,privileges:snapshot.privileges.filter(x=>x.id!==id)}); setMsg("Deleted."); }
    catch(e){ setErr(e instanceof Error?e.message:"Delete failed"); }
  },[snapshot]);

  const toggleEmp = useCallback((emp:EmployeeRow,checked:boolean)=>{
    setPrivForm(c=>{
      const tok=emp.employeeCode.trim()||emp.id;
      const ids=checked?[...new Set([...c.employeeIds,tok])]:c.employeeIds.filter(v=>v!==emp.id&&v!==emp.employeeCode);
      const names=checked?[...new Set([...c.employeeNames,emp.fullName])]:c.employeeNames.filter(v=>v!==emp.fullName);
      return {...c,employeeIds:ids,employeeNames:names};
    });
  },[]);

  const setSection=(next:AccessSection)=>{ const p=new URLSearchParams(searchParams.toString()); p.set("section",next); router.replace(`/access-control?${p.toString()}`); };

  // ── device selector ───────────────────────────────────────────────────────
  function DevSel({sel,onChange,area}:{sel:string[];onChange:(s:string[])=>void;area:string}){
    const list=devs.filter(d=>area==="All"||d.area===area);
    return (
      <div className="rounded-lg border border-[#c8ddf0] bg-[#f6fbff] p-3">
        <div className="mb-2 flex items-center justify-between">
          <span className="text-xs font-semibold text-[#1a4a73]">Devices ({sel.length} selected)</span>
          <button type="button" onClick={()=>onChange(list.map(d=>d.serialNumber))} className="text-[11px] text-[#1a4a73] hover:underline">Select All</button>
        </div>
        <div className="grid max-h-40 gap-1.5 overflow-y-auto sm:grid-cols-2">
          {list.length===0?<p className="col-span-2 py-3 text-center text-xs text-[#7a93ab]">No devices in this area.</p>:list.map(d=>{
            const on=sel.includes(d.serialNumber);
            return <button key={d.serialNumber} type="button" onClick={()=>onChange(on?sel.filter(s=>s!==d.serialNumber):[...sel,d.serialNumber])} className={`flex items-center gap-2 rounded-lg border px-2.5 py-1.5 text-left text-xs transition ${on?"border-[#1a4a73] bg-[#1a4a73] text-white":"border-[#c8ddf0] bg-white text-[#1a4a73] hover:bg-[#eef6ff]"}`}>
              <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${d.status==="online"?"bg-green-400":"bg-gray-300"}`}/>
              <span className="min-w-0 truncate font-medium">{d.name}</span>
              <span className={`ml-auto shrink-0 text-[10px] ${on?"text-blue-200":"text-[#7a93ab]"}`}>{d.serialNumber}</span>
            </button>;
          })}
        </div>
      </div>
    );
  }


  // ── render ────────────────────────────────────────────────────────────────
  const Btn = ({onClick,variant="edit",disabled,children}:{onClick:()=>void;variant?:"edit"|"del";disabled?:boolean;children:React.ReactNode})=>(
    <button type="button" onClick={onClick} disabled={disabled} className={`inline-flex items-center gap-1 rounded-lg border px-2.5 py-1.5 text-xs font-medium transition disabled:cursor-not-allowed disabled:opacity-40 ${variant==="del"?"border-red-200 text-red-600 hover:bg-red-50":"border-[#c8ddf0] text-[#1a4a73] hover:bg-[#eef6ff]"}`}>{children}</button>
  );

  if (loadingLicense) {
    return (
      <div className="flex min-h-[400px] flex-col items-center justify-center gap-3">
        <div className="h-10 w-10 animate-spin rounded-full border-4 border-[#1a4a73] border-t-transparent"></div>
        <p className="text-sm font-medium text-[#5a7a96]">Checking feature authorization...</p>
      </div>
    );
  }

  if (isLocked) {
    return (
      <div className="flex min-h-[500px] items-center justify-center px-4 py-8">
        <div className="relative w-full max-w-2xl overflow-hidden rounded-3xl border border-[#c8ddf0]/30 bg-gradient-to-br from-[#0b2b4e] via-[#103d6e] to-[#081e37] p-8 text-white shadow-2xl md:p-12">
          {/* Decorative glow/gradients */}
          <div className="absolute -left-20 -top-20 h-64 w-64 rounded-full bg-blue-500/20 blur-3xl"></div>
          <div className="absolute -bottom-20 -right-20 h-64 w-64 rounded-full bg-indigo-500/20 blur-3xl"></div>
          
          <div className="relative flex flex-col items-center text-center">
            {/* Lock Icon Box */}
            <div className="mb-6 flex h-20 w-20 items-center justify-center rounded-2xl bg-white/10 text-blue-300 shadow-[inset_0_1px_1px_rgba(255,255,255,0.2)] backdrop-blur-md">
              <Shield className="h-10 w-10" />
            </div>

            <h1 className="text-2xl font-bold tracking-tight md:text-3xl">Access Control Locked</h1>
            <p className="mt-3 max-w-md text-sm text-blue-100/80">
              Time Period, Access Group, Unlock Combination, and Privilege management are advanced professional security features.
            </p>

            {/* Feature List */}
            <div className="my-8 w-full max-w-md rounded-2xl bg-white/5 p-5 text-left text-sm border border-white/10 backdrop-blur-sm">
              <h2 className="mb-3 font-semibold text-blue-200 uppercase tracking-wider text-xs">Unlock access to:</h2>
              <ul className="space-y-2.5 text-blue-100/90">
                <li className="flex items-start gap-2.5">
                  <span className="mt-1.5 flex h-1.5 w-1.5 shrink-0 rounded-full bg-blue-400"></span>
                  <span><strong>Flexible Time Periods</strong>: Custom schedules for doors and gates.</span>
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-1.5 flex h-1.5 w-1.5 shrink-0 rounded-full bg-blue-400"></span>
                  <span><strong>Access Grouping</strong>: Group users with specialized verification options.</span>
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-1.5 flex h-1.5 w-1.5 shrink-0 rounded-full bg-blue-400"></span>
                  <span><strong>Multi-Card / Multi-User Combinations</strong>: Require multi-person verification to open specific locks.</span>
                </li>
                <li className="flex items-start gap-2.5">
                  <span className="mt-1.5 flex h-1.5 w-1.5 shrink-0 rounded-full bg-blue-400"></span>
                  <span><strong>Device privilege sync</strong>: Seamlessly sync permissions to SpeedFace and related controllers in real-time.</span>
                </li>
              </ul>
            </div>

            <button
              onClick={() => router.push("/settings?tab=license")}
              className="inline-flex items-center gap-2 rounded-xl bg-gradient-to-r from-blue-500 to-indigo-600 px-6 py-3 text-sm font-bold text-white shadow-lg transition hover:from-blue-600 hover:to-indigo-700 hover:shadow-xl focus:outline-none focus:ring-2 focus:ring-blue-400/50"
            >
              Activate License Now
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-bold text-[#0f3356]">Access Control</h1>
          <p className="text-sm text-[#5a7a96]">Manage time periods, access groups, combinations and employee privileges.</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {[{l:"Time Periods",v:counts.tp},{l:"Groups",v:counts.grp},{l:`Combinations ${counts.comb}/10`,v:counts.comb,warn:counts.comb>=10},{l:"Privileges",v:counts.priv}].map(({l,v,warn})=>(
            <span key={l} className={`rounded-full border px-3 py-1 text-xs font-semibold ${warn?"border-orange-200 bg-orange-50 text-orange-700":"border-[#c8ddf0] bg-white text-[#1a4a73]"}`}>{l}: {v}</span>
          ))}
        </div>
      </div>

      {err&&<div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{err} <button onClick={()=>setErr(null)} className="ml-2 text-red-400 hover:text-red-600">✕</button></div>}
      {msg&&<div className="rounded-lg border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-700">{msg} <button onClick={()=>setMsg(null)} className="ml-2 text-green-400 hover:text-green-600">✕</button></div>}

      <div className="grid gap-4 xl:grid-cols-[200px_1fr]">
        {/* Sidebar */}
        <aside className="rounded-xl border border-[#d5e8f7] bg-white shadow-sm overflow-hidden">
          <div className="border-b border-[#e8f2fb] bg-[#f5faff] px-3 py-2">
            <p className="text-[11px] font-bold uppercase tracking-wider text-[#4a7a9b]">Filter by Area</p>
          </div>
          <div className="p-2 space-y-0.5">
            {areaOpts.map(a=>(
              <button key={a} type="button" onClick={()=>setAreaFilter(a)} className={`flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm transition ${a===areaFilter?"bg-[#1a4a73] font-semibold text-white":"text-[#2d5a7e] hover:bg-[#eef6ff]"}`}>
                <span className={`h-2 w-2 rounded-full shrink-0 ${a===areaFilter?"bg-white":"bg-[#7ab3d9]"}`}/>
                <span className="truncate">{a}</span>
              </button>
            ))}
          </div>
        </aside>

        {/* Main panel */}
        <section className="rounded-xl border border-[#d5e8f7] bg-white shadow-sm">
          {/* Toolbar */}
          <div className="flex flex-wrap items-center gap-2 border-b border-[#e8f2fb] px-4 py-3">
            {SECTION_TABS.map(({key,label,icon:Icon})=>(
              <button key={key} type="button" onClick={()=>setSection(key)} className={`inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-semibold transition ${sel===key?"bg-[#1a4a73] text-white shadow-sm":"bg-[#f0f7ff] text-[#1a4a73] hover:bg-[#dceeff]"}`}>
                <Icon className="h-4 w-4"/>{label}
              </button>
            ))}
            <div className="ml-auto flex items-center gap-2">
              {sel === "privilege" && selectedEmpIds.length > 0 && (
                <button
                  type="button"
                  onClick={openBatchEditModal}
                  className="inline-flex h-8 items-center gap-1.5 rounded-lg bg-green-600 px-3 text-sm font-semibold text-white hover:bg-green-700 shadow-sm"
                >
                  <Edit3 className="h-3.5 w-3.5" />
                  Batch Edit ({selectedEmpIds.length})
                </button>
              )}
              <div className="relative">
                <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-[#7a93ab]"/>
                <input value={search} onChange={e=>setSearch(e.target.value)} placeholder="Search…" className="h-8 w-48 rounded-lg border border-[#c8ddf0] bg-white pl-8 pr-3 text-sm outline-none focus:border-[#4a90c4]"/>
              </div>
              <button type="button" disabled={sel==="combination"&&filt.comb.length>=10} onClick={()=>{if(sel==="time-period")openTp();else if(sel==="groups")openGrp();else if(sel==="combination")openComb();else openPriv();}} className="inline-flex h-8 items-center gap-1.5 rounded-lg bg-[#1a4a73] px-3 text-sm font-semibold text-white hover:bg-[#15395c] disabled:opacity-40">
                <Plus className="h-4 w-4"/>Add
              </button>
            </div>
          </div>

          {/* Tables */}
          <div className="overflow-x-auto">
            {/* Time Period table */}
            {sel==="time-period"&&(
              <table className="min-w-full text-sm">
                <thead><tr className="border-b border-[#e8f2fb] bg-[#f5faff] text-xs font-semibold uppercase tracking-wide text-[#4a7a9b]">
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("periodNo")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      No
                      {sortField === "periodNo" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("name")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Name
                      {sortField === "name" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("area")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Area
                      {sortField === "area" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("activeDays")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Active Days
                      {sortField === "activeDays" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">Time Range</th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("devices")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Devices
                      {sortField === "devices" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">Actions</th>
                </tr></thead>
                <tbody className="divide-y divide-[#eef5fc]">
                  {sortedTp.length===0?<tr><td colSpan={7} className="py-10 text-center text-[#7a93ab]">No time periods found.</td></tr>:sortedTp.map(item=>(
                    <tr key={item.id} className="hover:bg-[#fafcff]">
                      <td className="px-4 py-3 font-bold text-[#1a4a73]">{item.periodNo}</td>
                      <td className="px-4 py-3 font-medium">{item.name}</td>
                      <td className="px-4 py-3 text-[#5a7a96] text-xs">{item.area}</td>
                      <td className="px-4 py-3"><div className="flex flex-wrap gap-1">{item.days.map(d=><span key={d.dayOfWeek} className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${d.isActive?"bg-[#dceeff] text-[#1a4a73]":"bg-[#f0f4f8] text-[#9ab0c4]"}`}>{WEEKDAY_LABELS[d.dayOfWeek].slice(0,3)}</span>)}</div></td>
                      <td className="px-4 py-3 text-xs text-[#5a7a96]">{item.days.find(d=>d.isActive)?.startTime.slice(0,5)} – {item.days.find(d=>d.isActive)?.endTime.slice(0,5)}</td>
                      <td className="px-4 py-3 text-xs text-[#5a7a96]">{item.deviceSerials.length||"All"}</td>
                      <td className="px-4 py-3"><div className="flex gap-1.5"><Btn onClick={()=>openTp(item)}><Edit3 className="h-3.5 w-3.5"/>Edit</Btn><Btn variant="del" disabled={item.periodNo==="1"} onClick={()=>delTp(item.id)}><Trash2 className="h-3.5 w-3.5"/>Del</Btn></div></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {/* Access Group table */}
            {sel==="groups"&&(
              <table className="min-w-full text-sm">
                <thead><tr className="border-b border-[#e8f2fb] bg-[#f5faff] text-xs font-semibold uppercase tracking-wide text-[#4a7a9b]">
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("groupNo")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      No
                      {sortField === "groupNo" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("name")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Name
                      {sortField === "name" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("area")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Area
                      {sortField === "area" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("timePeriod1")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Time Period 1
                      {sortField === "timePeriod1" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("timePeriod2")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Time Period 2
                      {sortField === "timePeriod2" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("timePeriod3")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Time Period 3
                      {sortField === "timePeriod3" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("verification")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Verification
                      {sortField === "verification" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("holiday")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Holiday
                      {sortField === "holiday" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">Actions</th>
                </tr></thead>
                <tbody className="divide-y divide-[#eef5fc]">
                  {sortedGrp.length===0?<tr><td colSpan={9} className="py-10 text-center text-[#7a93ab]">No access groups found.</td></tr>:sortedGrp.map(item=>(
                    <tr key={item.id} className="hover:bg-[#fafcff]">
                      <td className="px-4 py-3 font-bold text-[#1a4a73]">{item.groupNo}</td>
                      <td className="px-4 py-3 font-medium">{item.name}</td>
                      <td className="px-4 py-3 text-[#5a7a96] text-xs">{item.area}</td>
                      <td className="px-4 py-3 text-xs">{tpLabel(snapshot.timePeriods,item.timePeriodIds[0])}</td>
                      <td className="px-4 py-3 text-xs">{tpLabel(snapshot.timePeriods,item.timePeriodIds[1])}</td>
                      <td className="px-4 py-3 text-xs">{tpLabel(snapshot.timePeriods,item.timePeriodIds[2])}</td>
                      <td className="px-4 py-3 text-xs">{labelForVerificationMode(item.verificationMode)}</td>
                      <td className="px-4 py-3 text-xs">{item.includeHoliday?"Yes":"No"}</td>
                      <td className="px-4 py-3"><div className="flex gap-1.5"><Btn onClick={()=>openGrp(item)}><Edit3 className="h-3.5 w-3.5"/>Edit</Btn><Btn variant="del" disabled={item.groupNo==="1"} onClick={()=>delGrp(item.id)}><Trash2 className="h-3.5 w-3.5"/>Del</Btn></div></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {/* Combination table */}
            {sel==="combination"&&(
              <table className="min-w-full text-sm">
                <thead><tr className="border-b border-[#e8f2fb] bg-[#f5faff] text-xs font-semibold uppercase tracking-wide text-[#4a7a9b]">
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("combinationNo")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      No
                      {sortField === "combinationNo" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("name")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Name
                      {sortField === "name" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">
                    <button type="button" onClick={() => handleSort("area")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                      Area
                      {sortField === "area" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                    </button>
                  </th>
                  <th className="px-4 py-3 text-left">Group 1</th><th className="px-4 py-3 text-left">Group 2</th><th className="px-4 py-3 text-left">Group 3</th><th className="px-4 py-3 text-left">Group 4</th><th className="px-4 py-3 text-left">Group 5</th>
                  <th className="px-4 py-3 text-left">Actions</th>
                </tr></thead>
                <tbody className="divide-y divide-[#eef5fc]">
                  {sortedComb.length===0?<tr><td colSpan={9} className="py-10 text-center text-[#7a93ab]">No combinations found.</td></tr>:sortedComb.map(item=>(
                    <tr key={item.id} className="hover:bg-[#fafcff]">
                      <td className="px-4 py-3 font-bold text-[#1a4a73]">{item.combinationNo}</td>
                      <td className="px-4 py-3 font-medium">{item.name}</td>
                      <td className="px-4 py-3 text-[#5a7a96] text-xs">{item.area}</td>
                      {item.groupIds.map((gid,i)=><td key={i} className="px-4 py-3 text-xs">{grpLabel(snapshot.groups,gid)}</td>)}
                      <td className="px-4 py-3"><div className="flex gap-1.5"><Btn onClick={()=>openComb(item)}><Edit3 className="h-3.5 w-3.5"/>Edit</Btn><Btn variant="del" disabled={item.combinationNo==="1"} onClick={()=>delComb(item.id)}><Trash2 className="h-3.5 w-3.5"/>Del</Btn></div></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            {/* Privilege table - employee centric */}
            {sel==="privilege"&&(()=> {
              return (
                <table className="min-w-full text-sm">
                  <thead><tr className="border-b border-[#e8f2fb] bg-[#f5faff] text-xs font-semibold uppercase tracking-wide text-[#4a7a9b]">
                    <th className="w-12 px-3 py-3 text-left">
                      <input
                        type="checkbox"
                        checked={sortedFilteredEmps.length > 0 && sortedFilteredEmps.every(e => selectedEmpIds.includes(e.id))}
                        onChange={e => {
                          if (e.target.checked) {
                            setSelectedEmpIds(prev => [...new Set([...prev, ...sortedFilteredEmps.map(x => x.id)])]);
                          } else {
                            setSelectedEmpIds(prev => prev.filter(id => !sortedFilteredEmps.some(x => x.id === id)));
                          }
                        }}
                        className="h-4 w-4 rounded border-[#c8ddf0] text-[#1a4a73] accent-[#1a4a73] outline-none"
                      />
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("employeeCode")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Employee ID
                        {sortField === "employeeCode" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("firstName")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        First Name
                        {sortField === "firstName" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("lastName")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Last Name
                        {sortField === "lastName" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("groupId")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Group
                        {sortField === "groupId" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("timezoneProfile")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Time Period Mode
                        {sortField === "timezoneProfile" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("slot1")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Time Period 1
                        {sortField === "slot1" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("slot2")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Time Period 2
                        {sortField === "slot2" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("slot3")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Time Period 3
                        {sortField === "slot3" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">
                      <button type="button" onClick={() => handleSort("verifyMode")} className="font-semibold hover:text-[--foreground] inline-flex items-center gap-0.5">
                        Verification Mode
                        {sortField === "verifyMode" && <span className="text-[10px]">{sortAsc ? "▲" : "▼"}</span>}
                      </button>
                    </th>
                    <th className="px-3 py-3 text-left">Action</th>
                  </tr></thead>
                  <tbody className="divide-y divide-[#eef5fc]">
                    {sortedFilteredEmps.length===0?<tr><td colSpan={11} className="py-10 text-center text-[#7a93ab]">No employees found.</td></tr>:sortedFilteredEmps.map(emp=>{
                      const grpNo = !emp.isActive ? 0 : (emp.groupId ?? 1);
                      const tz = emp.timezoneProfile ?? "0000000000000000";
                      const isPersonalTp = tz !== "0000000000000000" && tz.slice(4,8) !== "0000";
                      const tpMode = isPersonalTp ? "Personnel" : "Group";
                      const slot1 = isPersonalTp ? parseInt(tz.slice(4,8),10)||0 : 0;
                      const slot2 = isPersonalTp ? parseInt(tz.slice(8,12),10)||0 : 0;
                      const slot3 = isPersonalTp ? parseInt(tz.slice(12,16),10)||0 : 0;
                      const tpNo=(no:number)=>no>0?String(no):"0";
                      const verLabel = emp.verifyMode===-1||emp.verifyMode===undefined ? "Apply Group Mode" : labelForVerificationMode(String(emp.verifyMode) as AccessControlVerificationMode);
                      const nameParts = emp.fullName.trim().split(/\s+/);
                      const isChecked = selectedEmpIds.includes(emp.id);
                      return (
                        <tr key={emp.id} className={`hover:bg-[#fafcff] ${isChecked ? "bg-[#eaf5ff]" : ""}`}>
                          <td className="px-3 py-2.5">
                            <input
                              type="checkbox"
                              checked={isChecked}
                              onChange={e => {
                                if (e.target.checked) {
                                  setSelectedEmpIds(prev => [...prev, emp.id]);
                                } else {
                                  setSelectedEmpIds(prev => prev.filter(id => id !== emp.id));
                                }
                              }}
                              className="h-4 w-4 rounded border-[#c8ddf0] text-[#1a4a73] accent-[#1a4a73] outline-none"
                            />
                          </td>
                          <td className="px-3 py-2.5 font-medium text-[#1a4a73]">{emp.employeeCode}</td>
                          <td className="px-3 py-2.5">{nameParts[0]}</td>
                          <td className="px-3 py-2.5">{nameParts.slice(1).join(" ")||"—"}</td>
                          <td className="px-3 py-2.5 text-xs">
                            {grpNo === 0 ? (
                              <span className="inline-flex items-center gap-1 rounded-full bg-[#fde8e8] px-2 py-0.5 text-xs font-semibold text-[#c0392b]">
                                0 (Inactive)
                              </span>
                            ) : (
                              grpNo
                            )}
                          </td>
                          <td className="px-3 py-2.5 text-xs">{tpMode}</td>
                          <td className="px-3 py-2.5 text-xs">{tpNo(slot1)}</td>
                          <td className="px-3 py-2.5 text-xs">{tpNo(slot2)}</td>
                          <td className="px-3 py-2.5 text-xs">{tpNo(slot3)}</td>
                          <td className="px-3 py-2.5 text-xs">{verLabel}</td>
                          <td className="px-3 py-2.5">
                            <button type="button" onClick={()=>openEmpPriv(emp)} className="inline-flex items-center gap-1 rounded-lg border border-[#c8ddf0] px-2.5 py-1.5 text-xs font-medium text-[#1a4a73] hover:bg-[#eef6ff]">
                              <Edit3 className="h-3.5 w-3.5"/>Edit
                            </button>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              );
            })()}

            {sel === "duplicate-punch" && (
              <div className="p-4 space-y-4">
                {/* Toolbar row matching section toolbar style */}
                <div className="flex flex-wrap items-center gap-2 border-b border-[#e8f2fb] pb-4">
                  <div className="flex items-center gap-1.5 rounded-lg bg-[#f0f7ff] px-3 py-1.5 border border-[#d5e8f7]">
                    <Clock className="h-4 w-4 text-[#1a4a73]" />
                    <span className="text-sm font-semibold text-[#1a4a73]">Duplicate Punch Period</span>
                  </div>

                  {/* Period input */}
                  <div className="flex items-center gap-1.5 rounded-lg border border-[#c8ddf0] bg-white px-3 py-1.5">
                    <input
                      type="number"
                      min={0}
                      max={99}
                      value={dupPeriodMinutes}
                      onChange={(e) => setDupPeriodMinutes(Math.max(0, Math.min(99, parseInt(e.target.value) || 0)))}
                      className="w-14 text-center text-sm font-bold text-[#0f3356] outline-none bg-transparent"
                    />
                    <span className="text-xs text-[#5a7a96]">min</span>
                  </div>

                  {/* Info (!) button — same style as other dashboards */}
                  <div className="relative group">
                    <button
                      type="button"
                      onClick={() => setShowDupInfo((p) => !p)}
                      className="inline-flex h-6 w-6 items-center justify-center rounded-full border border-[#c8ddf0] bg-white text-xs font-bold text-[#1a4a73] hover:bg-[#eef6ff] transition"
                    >
                      i
                    </button>
                    {showDupInfo && (
                      <div className="absolute left-0 top-8 z-20 w-72 rounded-lg border border-[#d5e8f7] bg-white p-3 shadow-lg text-xs text-[#2d5a7e] leading-relaxed">
                        <p className="font-semibold text-[#0f3356] mb-1">Device Duplicate Punch Period</p>
                        <p>Prevents repeat punches on the terminal within the set minutes. The device shows <span className="font-semibold text-orange-600">"Duplicate Punch"</span> and ignores the scan. Set to <strong>0</strong> to disable.</p>
                      </div>
                    )}
                  </div>

                  <div className="ml-auto flex items-center gap-2">
                    {/* Search */}
                    <div className="relative">
                      <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-[#7a93ab]" />
                      <input
                        value={dupDevSearch}
                        onChange={(e) => setDupDevSearch(e.target.value)}
                        placeholder="Search…"
                        className="h-8 w-40 rounded-lg border border-[#c8ddf0] bg-white pl-8 pr-3 text-xs outline-none focus:border-[#4a90c4]"
                      />
                    </div>

                    {/* Save button */}
                    <button
                      type="button"
                      disabled={savingDupPeriod}
                      onClick={saveDupPeriod}
                      className="inline-flex h-8 items-center gap-1.5 rounded-lg bg-[#1a4a73] px-3 text-sm font-semibold text-white hover:bg-[#15395c] disabled:opacity-50"
                    >
                      {savingDupPeriod ? "Saving..." : "Save & Push"}
                    </button>
                  </div>
                </div>

                {/* Device selection counter + select-all */}
                <div className="flex items-center justify-between">
                  <p className="text-xs text-[#5a7a96]">
                    {targetSerials.length === 0
                      ? <><span className="font-semibold text-[#1a4a73]">All devices</span> will receive this setting.</>
                      : <><span className="font-semibold text-[#1a4a73]">{targetSerials.length}</span> device{targetSerials.length !== 1 ? "s" : ""} selected.</>
                    }
                    {" "}{filteredDevices.length} shown.
                  </p>
                  <div className="flex gap-1.5">
                    <button
                      type="button"
                      onClick={() => setTargetSerials(filteredDevices.map((d) => d.serialNumber))}
                      className="rounded border border-[#c8ddf0] bg-white px-2.5 py-1 text-xs font-semibold text-[#1a4a73] hover:bg-[#eef6ff]"
                    >
                      Select All
                    </button>
                    <button
                      type="button"
                      onClick={() => setTargetSerials([])}
                      className="rounded border border-[#c8ddf0] bg-white px-2.5 py-1 text-xs font-semibold text-[#1a4a73] hover:bg-[#eef6ff]"
                    >
                      Clear
                    </button>
                  </div>
                </div>

                {/* Device table — same style as other section tables */}
                <div className="overflow-x-auto">
                  <table className="min-w-full text-sm">
                    <thead>
                      <tr className="border-b border-[#e8f2fb] bg-[#f5faff] text-xs font-semibold uppercase tracking-wide text-[#4a7a9b]">
                        <th className="w-10 px-4 py-3 text-center">
                          <input
                            type="checkbox"
                            checked={filteredDevices.length > 0 && filteredDevices.every((d) => targetSerials.includes(d.serialNumber))}
                            onChange={(e) => e.target.checked
                              ? setTargetSerials((prev) => Array.from(new Set([...prev, ...filteredDevices.map((d) => d.serialNumber)])))
                              : setTargetSerials((prev) => prev.filter((s) => !filteredDevices.some((d) => d.serialNumber === s)))
                            }
                            className="h-3.5 w-3.5 accent-[#1a4a73]"
                          />
                        </th>
                        <th className="px-4 py-3 text-left">Device Name</th>
                        <th className="px-4 py-3 text-left">Serial No</th>
                        <th className="px-4 py-3 text-left">Area</th>
                        <th className="px-4 py-3 text-left">Dup. Punch Period</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-[#eef5fc]">
                      {filteredDevices.length === 0 ? (
                        <tr><td colSpan={5} className="py-10 text-center text-[#7a93ab]">No devices found.</td></tr>
                      ) : filteredDevices.map((dev) => {
                        const isChk = targetSerials.includes(dev.serialNumber);
                        const savedVal = snapshot.deviceDuplicatePunchPeriodMinutes ?? 0;
                        const periodLabel = savedVal === 0 ? "Off" : `${savedVal} min`;
                        const periodApplied = targetSerials.length === 0 || targetSerials.includes(dev.serialNumber);
                        return (
                          <tr
                            key={dev.serialNumber}
                            onClick={() => setTargetSerials((prev) => isChk ? prev.filter((s) => s !== dev.serialNumber) : [...prev, dev.serialNumber])}
                            className={`cursor-pointer hover:bg-[#fafcff] ${isChk ? "bg-[#f4f9ff]" : ""}`}
                          >
                            <td className="px-4 py-3 text-center" onClick={(e) => e.stopPropagation()}>
                              <input
                                type="checkbox"
                                checked={isChk}
                                onChange={(e) => setTargetSerials((prev) => e.target.checked ? [...prev, dev.serialNumber] : prev.filter((s) => s !== dev.serialNumber))}
                                className="h-3.5 w-3.5 accent-[#1a4a73]"
                              />
                            </td>
                            <td className="px-4 py-3 font-medium text-[#0f3356]">{dev.name || "—"}</td>
                            <td className="px-4 py-3 font-mono text-xs font-bold text-[#1a4a73]">{dev.serialNumber}</td>
                            <td className="px-4 py-3 text-xs text-[#5a7a96]">{dev.area || "—"}</td>
                            <td className="px-4 py-3">
                              {periodApplied
                                ? <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                                    savedVal === 0 ? "bg-[#f0f4f8] text-[#7a93ab]" : "bg-[#dceeff] text-[#1a4a73]"
                                  }`}>{periodLabel}</span>
                                : <span className="text-xs text-[#9ab0c4]">—</span>
                              }
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        </section>
      </div>


      {/* ── Time Period Modal ── */}
      {modal?.kind==="time-period"&&(
        <Modal
          title={tpForm.id?"Edit Time Period":"New Time Period"}
          onClose={closeModal}
          onSave={saveTp}
          saveLabel="Save"
          onReset={() => {
            const area = tpForm.area || firstArea;
            setTpForm(c => ({
              ...c,
              periodNo: "1",
              name: "Time Period 1",
              remark: "Default",
              days: createDays(),
              deviceSerials: devs.filter(d => (d.area || "Default Area") === area).map(d => d.serialNumber)
            }));
          }}
        >
          <div className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <F label="Area"><select value={tpForm.area} onChange={e=>{setTpForm(c=>({...c,area:e.target.value,department:"All"}));}} className={ic}>{areaOpts.filter(a=>a!=="All").map(a=><option key={a}>{a}</option>)}</select></F>
              <F label="Period No (1–50)"><input value={tpForm.periodNo} onChange={e=>setTpForm(c=>({...c,periodNo:e.target.value}))} placeholder="Auto" className={ic}/></F>
              <F label="Name"><input value={tpForm.name} onChange={e=>setTpForm(c=>({...c,name:e.target.value}))} className={ic}/></F>
            </div>
            <div>
              <div className="mb-2 flex items-center justify-between">
                <span className="text-xs font-semibold text-[#1a4a73]">Day Schedule</span>
                <button type="button" onClick={()=>setTpForm(c=>({...c,days:c.days.map(d=>({...d,startTime:"00:00:00",endTime:"23:59:00",isActive:true}))}))} className="text-xs text-[#1a4a73] hover:underline font-medium">Reset all to 00:00–23:59</button>
              </div>
              <div className="overflow-x-auto rounded-lg border border-[#d5e8f7]">
                <table className="min-w-full text-sm">
                  <thead><tr className="bg-[#f5faff] text-xs text-[#4a7a9b]"><th className="px-3 py-2 text-left">Day</th><th className="px-3 py-2">Active</th><th className="px-3 py-2 text-left">Start</th><th className="px-3 py-2 text-left">End</th><th className="px-3 py-2 text-left">Copy</th></tr></thead>
                  <tbody className="divide-y divide-[#eef5fc]">
                    {tpForm.days.map((d,i)=>(
                      <tr key={i} className={d.isActive?"":"opacity-50"}>
                        <td className="px-3 py-2 font-medium">{WEEKDAY_LABELS[i]}</td>
                        <td className="px-3 py-2 text-center"><input type="checkbox" checked={d.isActive} onChange={e=>setTpForm(c=>({...c,days:c.days.map(x=>x.dayOfWeek===i?{...x,isActive:e.target.checked}:x)}))} className="h-4 w-4 accent-[#1a4a73]"/></td>
                        <td className="px-3 py-2"><input type="time" value={d.startTime.slice(0,5)} onChange={e=>setTpForm(c=>({...c,days:c.days.map(x=>x.dayOfWeek===i?{...x,startTime:e.target.value+":00"}:x)}))} className={ic+" w-28"}/></td>
                        <td className="px-3 py-2"><input type="time" value={d.endTime.slice(0,5)} onChange={e=>setTpForm(c=>({...c,days:c.days.map(x=>x.dayOfWeek===i?{...x,endTime:e.target.value+":00"}:x)}))} className={ic+" w-28"}/></td>
                        <td className="px-3 py-2"><button type="button" onClick={()=>setTpForm(c=>({...c,days:c.days.map(x=>({...x,startTime:d.startTime,endTime:d.endTime}))}))} className="rounded border border-[#c8ddf0] px-2 py-1 text-xs text-[#1a4a73] hover:bg-[#eef6ff]">Copy to all</button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
            <F label="Target Devices"><DevSel sel={tpForm.deviceSerials} onChange={s=>setTpForm(c=>({...c,deviceSerials:s}))} area={tpForm.area}/></F>
            <F label="Remark"><input value={tpForm.remark} onChange={e=>setTpForm(c=>({...c,remark:e.target.value}))} className={ic}/></F>
          </div>
        </Modal>
      )}

      {/* ── Access Group Modal ── */}
      {modal?.kind==="group"&&(
        <Modal
          title={grpForm.id?"Edit Access Group":"New Access Group"}
          onClose={closeModal}
          onSave={saveGrp}
          saveLabel="Save"
          onReset={() => {
            const area = grpForm.area || firstArea;
            const areaTps = snapshot.timePeriods.filter(t => t.area === area);
            const tp1Id = areaTps.find(t => t.periodNo === "1")?.id ?? areaTps[0]?.id ?? defaultTpId(snapshot.timePeriods);
            setGrpForm(c => ({
              ...c,
              groupNo: "1",
              name: "Access Group 1",
              verificationMode: "0",
              timePeriodIds: [tp1Id, "", ""],
              includeHoliday: false,
              remark: "Default",
              deviceSerials: devs.filter(d => (d.area || "Default Area") === area).map(d => d.serialNumber)
            }));
          }}
        >
          <div className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2">
              <F label="Area"><select value={grpForm.area} onChange={e=>{setGrpForm(c=>({...c,area:e.target.value,department:"All"}));}} className={ic}>{areaOpts.filter(a=>a!=="All").map(a=><option key={a}>{a}</option>)}</select></F>
              <F label="Group No (1–50)"><input value={grpForm.groupNo} onChange={e=>setGrpForm(c=>({...c,groupNo:e.target.value}))} placeholder="Auto" className={ic}/></F>
              <F label="Name"><input value={grpForm.name} onChange={e=>setGrpForm(c=>({...c,name:e.target.value}))} className={ic}/></F>
              <F label="Verification Mode"><select value={grpForm.verificationMode} onChange={e=>setGrpForm(c=>({...c,verificationMode:e.target.value as AccessControlVerificationMode}))} className={ic}>{VERIFY_OPTS.map(o=><option key={o.value} value={o.value}>{o.label}</option>)}</select></F>
              {([0,1,2] as const).map(i=>(
                <F key={i} label={`Time Period ${i+1}`}><select value={grpForm.timePeriodIds[i]} onChange={e=>setGrpForm(c=>({...c,timePeriodIds:c.timePeriodIds.map((v,j)=>j===i?e.target.value:v) as [string,string,string]}))} className={ic}><option value="">— None —</option>{snapshot.timePeriods.filter(t => t.area === grpForm.area).map(t=><option key={t.id} value={t.id}>{t.periodNo} - {t.name}</option>)}</select></F>
              ))}
            </div>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={grpForm.includeHoliday} onChange={e=>setGrpForm(c=>({...c,includeHoliday:e.target.checked}))} className="h-4 w-4 accent-[#1a4a73]"/>Include holiday</label>
            <F label="Target Devices"><DevSel sel={grpForm.deviceSerials} onChange={s=>setGrpForm(c=>({...c,deviceSerials:s}))} area={grpForm.area}/></F>
            <F label="Remark"><input value={grpForm.remark} onChange={e=>setGrpForm(c=>({...c,remark:e.target.value}))} className={ic}/></F>
          </div>
        </Modal>
      )}

      {/* ── Combination Modal ── */}
      {modal?.kind==="combination"&&(
        <Modal
          title={combForm.id?"Edit Combination":"New Combination"}
          onClose={closeModal}
          onSave={saveComb}
          saveLabel="Save"
          onReset={() => {
            const area = combForm.area || firstArea;
            const areaGrps = snapshot.groups.filter(g => g.area === area);
            const grp1Id = areaGrps.find(g => g.groupNo === "1")?.id ?? areaGrps[0]?.id ?? defaultGrpId(snapshot.groups);
            setCombForm(c => ({
              ...c,
              combinationNo: "1",
              name: "Combination 1",
              groupIds: [grp1Id, "", "", "", ""],
              remark: "Default",
              deviceSerials: devs.filter(d => (d.area || "Default Area") === area).map(d => d.serialNumber)
            }));
          }}
        >
          <div className="space-y-4">
            {filt.comb.length>=10&&!combForm.id&&<div className="rounded-lg border border-orange-200 bg-orange-50 px-3 py-2 text-sm text-orange-700">⚠ Maximum 10 combinations per device.</div>}
            <div className="grid gap-3 sm:grid-cols-2">
              <F label="Area"><select value={combForm.area} onChange={e=>{setCombForm(c=>({...c,area:e.target.value,department:"All"}));}} className={ic}>{areaOpts.filter(a=>a!=="All").map(a=><option key={a}>{a}</option>)}</select></F>
              <F label="Combination No (1–10)"><input value={combForm.combinationNo} onChange={e=>setCombForm(c=>({...c,combinationNo:e.target.value}))} placeholder="Auto" className={ic}/></F>
              <F label="Name"><input value={combForm.name} onChange={e=>setCombForm(c=>({...c,name:e.target.value}))} className={ic}/></F>
            </div>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {([0,1,2,3,4] as const).map(i=>(
                <F key={i} label={`Group ${i+1}`}><select value={combForm.groupIds[i]} onChange={e=>setCombForm(c=>({...c,groupIds:c.groupIds.map((v,j)=>j===i?e.target.value:v) as [string,string,string,string,string]}))} className={ic}><option value="">— None —</option>{snapshot.groups.filter(g => g.area === combForm.area).map(g=><option key={g.id} value={g.id}>{g.groupNo} - {g.name}</option>)}</select></F>
              ))}
            </div>
            <F label="Target Devices"><DevSel sel={combForm.deviceSerials} onChange={s=>setCombForm(c=>({...c,deviceSerials:s}))} area={combForm.area}/></F>
            <F label="Remark"><input value={combForm.remark} onChange={e=>setCombForm(c=>({...c,remark:e.target.value}))} className={ic}/></F>
          </div>
        </Modal>
      )}

      {/* ── Privilege Modal ── */}
      {modal?.kind==="privilege"&&(
        <Modal
          title={privForm.id?"Edit Privilege":"New Privilege"}
          onClose={closeModal}
          onSave={savePriv}
          saveLabel="Confirm"
          onReset={() => {
            const area = privForm.area || firstArea;
            const areaGrps = snapshot.groups.filter(g => g.area === area);
            const areaTps = snapshot.timePeriods.filter(t => t.area === area);
            const grp1Id = areaGrps.find(g => g.groupNo === "1")?.id ?? areaGrps[0]?.id ?? defaultGrpId(snapshot.groups);
            const tp1Id = areaTps.find(t => t.periodNo === "1")?.id ?? areaTps[0]?.id ?? defaultTpId(snapshot.timePeriods);
            setPrivForm(c => ({
              ...c,
              groupId: grp1Id,
              verificationMode: "group",
              verificationModeValue: "0",
              timePeriodMode: "group",
              timePeriodIds: [tp1Id, "", ""],
              remark: "Default",
              deviceSerials: devs.filter(d => (d.area || "Default Area") === area).map(d => d.serialNumber)
            }));
          }}
        >
          <div className="space-y-4">
            {/* Filter + Group */}
            <div className="grid gap-3 sm:grid-cols-2">
              <F label="Area"><select value={privForm.area} onChange={e=>setPrivForm(c=>({...c,area:e.target.value}))} className={ic}>{areaOpts.map(a=><option key={a}>{a}</option>)}</select></F>
              <F label="Access Group *"><select value={privForm.groupId} onChange={e=>setPrivForm(c=>({...c,groupId:e.target.value}))} className={ic}><option value="">— Select —</option>{snapshot.groups.filter(g => g.area === privForm.area).map(g=><option key={g.id} value={g.id}>{g.groupNo} - {g.name}</option>)}</select></F>
            </div>

            {/* Verification + Time Period panels */}
            <div className="grid gap-3 sm:grid-cols-2">
              <div className="rounded-lg border border-[#c8ddf0] bg-[#f5faff] p-3 space-y-2">
                <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Verification Mode</p>
                <div className="flex gap-4">
                  {(["manual","group"] as const).map(v=><label key={v} className="flex cursor-pointer items-center gap-1.5 text-sm"><input type="radio" name="vm" checked={privForm.verificationMode===v} onChange={()=>setPrivForm(c=>({...c,verificationMode:v}))} className="accent-[#1a4a73]"/>{v==="manual"?"Personnel":"Group"}</label>)}
                </div>
                <select value={privForm.verificationMode==="group"?"-1":privForm.verificationModeValue} disabled={privForm.verificationMode==="group"} onChange={e=>setPrivForm(c=>({...c,verificationModeValue:e.target.value as AccessControlVerificationMode}))} className={ic+" disabled:bg-[#eef2f6] disabled:text-[#9ab0c4]"}>
                  <option value="-1">Any (Apply Group Mode)</option>
                  {VERIFY_OPTS.filter(o=>o.value!=="-1").map(o=><option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </div>
              <div className="rounded-lg border border-[#c8ddf0] bg-[#f5faff] p-3 space-y-2">
                <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Time Period</p>
                <div className="flex gap-4">
                  {(["manual","group"] as const).map(v=><label key={v} className="flex cursor-pointer items-center gap-1.5 text-sm"><input type="radio" name="tpm" checked={privForm.timePeriodMode===v} onChange={()=>setPrivForm(c=>({...c,timePeriodMode:v}))} className="accent-[#1a4a73]"/>{v==="manual"?"Personnel":"Group"}</label>)}
                </div>
                {([0,1,2] as const).map(i=>(
                  <div key={i}>
                    <p className="mb-1 text-[11px] text-[#5a7a96]">Time Period {i+1}{i===0?" *":""}</p>
                    <select value={privForm.timePeriodIds[i]} disabled={privForm.timePeriodMode==="group"} onChange={e=>setPrivForm(c=>({...c,timePeriodIds:c.timePeriodIds.map((v,j)=>j===i?e.target.value:v) as [string,string,string]}))} className={ic+" disabled:bg-[#eef2f6] disabled:text-[#9ab0c4]"}>
                      <option value="">----------</option>
                      {snapshot.timePeriods.filter(t => t.area === privForm.area).map(t=><option key={t.id} value={t.id}>{t.periodNo} - {t.name}</option>)}
                    </select>
                  </div>
                ))}
              </div>
            </div>

            {/* Employee table */}
            <div>
              <div className="mb-1.5 flex items-center justify-between">
                <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Select Employees ({privForm.employeeIds.length} selected)</p>
                <button type="button" onClick={()=>{ if(privForm.employeeIds.length===privEmps.length) privEmps.forEach(e=>toggleEmp(e,false)); else privEmps.forEach(e=>toggleEmp(e,true)); }} className="text-xs text-[#1a4a73] hover:underline font-medium">{privForm.employeeIds.length===privEmps.length?"Deselect All":"Select All"}</button>
              </div>
              <div className="max-h-56 overflow-auto rounded-lg border border-[#d5e8f7]">
                {privEmps.length===0?<p className="py-6 text-center text-sm text-[#7a93ab]">No employees in this area/department.</p>:(
                  <table className="min-w-full text-xs">
                    <thead className="sticky top-0 bg-[#f5faff] text-[#4a7a9b]"><tr><th className="w-8 px-3 py-2"/><th className="px-3 py-2 text-left">ID</th><th className="px-3 py-2 text-left">Name</th><th className="px-3 py-2 text-left">Department</th><th className="px-3 py-2 text-left">Group</th></tr></thead>
                    <tbody className="divide-y divide-[#eef5fc]">
                      {privEmps.map(e=>{
                        const on=privForm.employeeIds.includes(e.id)||privForm.employeeIds.includes(e.employeeCode);
                        return <tr key={e.id} className={on?"bg-[#eaf5ff]":"hover:bg-[#fafcff]"}>
                          <td className="px-3 py-2"><input type="checkbox" checked={on} onChange={ev=>toggleEmp(e,ev.target.checked)} className="h-3.5 w-3.5 accent-[#1a4a73]"/></td>
                          <td className="px-3 py-2 font-medium text-[#1a4a73]">{e.employeeCode}</td>
                          <td className="px-3 py-2">{e.fullName}</td>
                          <td className="px-3 py-2 text-[#5a7a96]">{e.department}</td>
                          <td className="px-3 py-2 text-[#5a7a96]">{selPrivGrp?.groupNo??"1"}</td>
                        </tr>;
                      })}
                    </tbody>
                  </table>
                )}
              </div>
            </div>

            <F label="Target Devices"><DevSel sel={privForm.deviceSerials} onChange={s=>setPrivForm(c=>({...c,deviceSerials:s}))} area={privForm.area}/></F>
          </div>
        </Modal>
      )}

      {/* ── Per-Employee Privilege Modal ── */}
      {modal?.kind==="emp-privilege"&&(
        <Modal title={`Edit Privilege — ${empPrivForm.empName} (${empPrivForm.empCode})`} onClose={closeModal} onSave={saveEmpPriv} saveLabel={empPrivSaving?"Saving…":"Confirm"}>
          {(()=>{
            const emp = emps.find(e => e.id === modal.empId);
            const empArea = emp?.area || "";
            return (
              <div className="space-y-4 max-w-xl">
                <div className="rounded-lg border border-[#c8ddf0] bg-[#f0f7ff] px-4 py-2.5 text-sm text-[#1a4a73]">
                  Employee: <strong>{empPrivForm.empName}</strong> &nbsp;|&nbsp; ID: <strong>{empPrivForm.empCode}</strong>
                </div>
                <F label="Access Group *">
                  <select value={empPrivForm.groupId} onChange={e=>setEmpPrivForm(c=>({...c,groupId:e.target.value}))} className={ic}>
                    <option value="0">0 - No Access Group (Inactive / Locked)</option>
                    {!snapshot.groups.some(g=>String(g.groupNo)===empPrivForm.groupId && g.area === empArea) && empPrivForm.groupId !== "0" && <option value={empPrivForm.groupId}>{empPrivForm.groupId} - Group {empPrivForm.groupId}</option>}
                    {snapshot.groups.filter(g => g.area === empArea).map(g=><option key={g.id} value={String(g.groupNo)}>{g.groupNo} - {g.name}</option>)}
                  </select>
                </F>
                <div className="grid gap-3 sm:grid-cols-2">
                  <div className="rounded-lg border border-[#c8ddf0] bg-[#f5faff] p-3 space-y-2">
                    <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Verification Mode</p>
                    <div className="flex gap-4">
                      {(["manual","group"] as const).map(v=>(
                        <label key={v} className="flex cursor-pointer items-center gap-1.5 text-sm">
                          <input type="radio" name="epvm" checked={v==="group"?empPrivForm.verifyMode==="-1":empPrivForm.verifyMode!=="-1"} onChange={()=>setEmpPrivForm(c=>({...c,verifyMode:v==="group"?"-1":"0"}))} className="accent-[#1a4a73]"/>
                          {v==="manual"?"Personnel":"Group"}
                        </label>
                      ))}
                    </div>
                    <select value={empPrivForm.verifyMode==="-1"?"-1":empPrivForm.verifyMode} disabled={empPrivForm.verifyMode==="-1"} onChange={e=>setEmpPrivForm(c=>({...c,verifyMode:e.target.value}))} className={ic+" disabled:bg-[#eef2f6] disabled:text-[#9ab0c4]"}>
                      <option value="-1">Any (Apply Group Mode)</option>
                      {VERIFY_OPTS.filter(o=>o.value!=="-1").map(o=><option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                  </div>
                  <div className="rounded-lg border border-[#c8ddf0] bg-[#f5faff] p-3 space-y-2">
                    <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Time Period</p>
                    <div className="flex gap-4">
                      {(["manual","group"] as const).map(v=>(
                        <label key={v} className="flex cursor-pointer items-center gap-1.5 text-sm">
                          <input type="radio" name="eptpm" checked={empPrivForm.timePeriodMode===v} onChange={()=>setEmpPrivForm(c=>({...c,timePeriodMode:v}))} className="accent-[#1a4a73]"/>
                          {v==="manual"?"Personnel":"Group"}
                        </label>
                      ))}
                    </div>
                    {([0,1,2] as const).map(i=>(
                      <div key={i}>
                        <p className="mb-1 text-[11px] text-[#5a7a96]">Time Period {i+1}{i===0?" *":""}</p>
                        <select value={empPrivForm.timePeriodIds[i]} disabled={empPrivForm.timePeriodMode==="group"} onChange={e=>setEmpPrivForm(c=>({...c,timePeriodIds:c.timePeriodIds.map((v,j)=>j===i?e.target.value:v) as [string,string,string]}))} className={ic+" disabled:bg-[#eef2f6] disabled:text-[#9ab0c4]"}>
                          <option value="">----------</option>
                          {snapshot.timePeriods.filter(t => t.area === empArea).map(t=><option key={t.id} value={t.id}>{t.periodNo} - {t.name}</option>)}
                        </select>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            );
          })()}
        </Modal>
      )}

      {/* ── Batch Privilege Modal ── */}
      {batchModalOpen && (
        <Modal
          title={`Batch Edit Privilege (${selectedEmpIds.length} employees selected)`}
          onClose={() => setBatchModalOpen(false)}
          onSave={saveBatchEdit}
          saveLabel={batchSaving ? "Saving..." : "Confirm"}
        >
          <div className="space-y-4 max-w-xl">
            <div className="rounded-lg border border-[#c8ddf0] bg-[#f0f7ff] px-4 py-2.5 text-sm text-[#1a4a73]">
              You are updating privileges for <strong>{selectedEmpIds.length}</strong> selected employees.
            </div>
            <F label="Access Group *">
              <select
                value={batchForm.groupId}
                onChange={e => setBatchForm(c => ({ ...c, groupId: e.target.value }))}
                className={ic}
              >
                <option value="0">0 - No Access Group (Inactive / Locked)</option>
                {snapshot.groups.filter(g => areaFilter === "All" || g.area === areaFilter).map(g => (
                  <option key={g.id} value={String(g.groupNo)}>
                    {g.groupNo} - {g.name}
                  </option>
                ))}
              </select>
            </F>
            <div className="grid gap-3 sm:grid-cols-2">
              <div className="rounded-lg border border-[#c8ddf0] bg-[#f5faff] p-3 space-y-2">
                <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Verification Mode</p>
                <div className="flex gap-4">
                  {(["manual", "group"] as const).map(v => (
                    <label key={v} className="flex cursor-pointer items-center gap-1.5 text-sm">
                      <input
                        type="radio"
                        name="batch-vm"
                        checked={v === "group" ? batchForm.verifyMode === "-1" : batchForm.verifyMode !== "-1"}
                        onChange={() => setBatchForm(c => ({ ...c, verifyMode: v === "group" ? "-1" : "0" }))}
                        className="accent-[#1a4a73]"
                      />
                      {v === "manual" ? "Personnel" : "Group"}
                    </label>
                  ))}
                </div>
                <select
                  value={batchForm.verifyMode === "-1" ? "-1" : batchForm.verifyMode}
                  disabled={batchForm.verifyMode === "-1"}
                  onChange={e => setBatchForm(c => ({ ...c, verifyMode: e.target.value }))}
                  className={ic + " disabled:bg-[#eef2f6] disabled:text-[#9ab0c4]"}
                >
                  <option value="-1">Any (Apply Group Mode)</option>
                  {VERIFY_OPTS.filter(o => o.value !== "-1").map(o => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </select>
              </div>
              <div className="rounded-lg border border-[#c8ddf0] bg-[#f5faff] p-3 space-y-2">
                <p className="text-xs font-bold uppercase tracking-wide text-[#1a4a73]">Time Period</p>
                <div className="flex gap-4">
                  {(["manual", "group"] as const).map(v => (
                    <label key={v} className="flex cursor-pointer items-center gap-1.5 text-sm">
                      <input
                        type="radio"
                        name="batch-tpm"
                        checked={batchForm.timePeriodMode === v}
                        onChange={() => setBatchForm(c => ({ ...c, timePeriodMode: v }))}
                        className="accent-[#1a4a73]"
                      />
                      {v === "manual" ? "Personnel" : "Group"}
                    </label>
                  ))}
                </div>
                {([0, 1, 2] as const).map(i => (
                  <div key={i}>
                    <p className="mb-1 text-[11px] text-[#5a7a96]">Time Period {i + 1}{i === 0 ? " *" : ""}</p>
                    <select
                      value={batchForm.timePeriodIds[i]}
                      disabled={batchForm.timePeriodMode === "group"}
                      onChange={e =>
                        setBatchForm(c => ({
                          ...c,
                          timePeriodIds: c.timePeriodIds.map((v, j) => (j === i ? e.target.value : v)) as [
                            string,
                            string,
                            string
                          ],
                        }))
                      }
                      className={ic + " disabled:bg-[#eef2f6] disabled:text-[#9ab0c4]"}
                    >
                      <option value="">----------</option>
                      {snapshot.timePeriods.filter(t => areaFilter === "All" || t.area === areaFilter).map(t => (
                        <option key={t.id} value={t.id}>
                          {t.periodNo} - {t.name}
                        </option>
                      ))}
                    </select>
                  </div>
                ))}
              </div>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}

// ── Field helper ──────────────────────────────────────────────────────────────
function F({label,children,cls}:{label:string;children:React.ReactNode;cls?:string}){
  return <label className={`block space-y-1 ${cls??""}`}><span className="text-xs font-semibold text-[#2d5a7e]">{label}</span>{children}</label>;
}

// ── Modal helper ──────────────────────────────────────────────────────────────
function Modal({title,children,onClose,onSave,onReset,saveLabel}:{title:string;children:React.ReactNode;onClose:()=>void;onSave:()=>void;onReset?:()=>void;saveLabel:string}){
  useEffect(()=>{
    const h=(e:KeyboardEvent)=>{ if(e.key==="Escape") onClose(); };
    window.addEventListener("keydown",h);
    return ()=>window.removeEventListener("keydown",h);
  },[onClose]);
  return (
    <div className="fixed inset-0 z-50 overflow-y-auto bg-black/50 backdrop-blur-sm p-4 sm:p-6 grid place-items-center">
      <div className="relative my-auto flex max-h-[88vh] w-full max-w-3xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl border border-[#d5e8f7]">
        <div className="flex items-center justify-between border-b border-[#e8f2fb] px-5 py-3.5 bg-[#f8fbfe]">
          <h2 className="text-base font-bold text-[#0f3356]">{title}</h2>
          <div className="flex items-center gap-2">
            {onReset && (
              <button
                type="button"
                onClick={onReset}
                className="inline-flex items-center gap-1.5 rounded-lg border border-[#c8ddf0] bg-white px-3 py-1.5 text-xs font-semibold text-[#1a4a73] hover:bg-[#eef6ff] transition shadow-sm"
              >
                <RotateCcw className="h-3.5 w-3.5 text-[#1a4a73]" />
                Reset to Default
              </button>
            )}
            <button type="button" onClick={onClose} className="rounded-lg border border-[#d5e8f7] p-1.5 text-[#5a7a96] hover:bg-[#f0f7ff] transition"><X className="h-4 w-4"/></button>
          </div>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">{children}</div>
        <div className="flex items-center justify-end gap-2 border-t border-[#e8f2fb] px-5 py-3 bg-[#f8fbfe]">
          <button type="button" onClick={onClose} className="rounded-lg border border-[#c8ddf0] px-4 py-2 text-sm font-semibold text-[#2d5a7e] hover:bg-[#f0f7ff] transition">Cancel</button>
          <button type="button" onClick={onSave} className="rounded-lg bg-[#1a4a73] px-5 py-2 text-sm font-semibold text-white hover:bg-[#15395c] transition shadow-sm">{saveLabel}</button>
        </div>
      </div>
    </div>
  );
}

