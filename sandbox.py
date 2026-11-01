"""Host-side command sandbox for model-requested shell commands (Windows AppContainer).

Every command runs as a child of this (trusted) process inside a Windows AppContainer: an OS-enforced token that can open
only objects whose ACL grants its SID (or ALL APPLICATION PACKAGES, which Windows grants read-only on its system folders).
The engine grants the container exactly the conversation's folders (write or read) with sync_grants; anything else on
disk is unreadable or read-only. The container has no network capability unless one is granted for the command, and
loopback stays blocked even then. Child processes inherit the token; a kill-on-close Job holds the whole tree, so a
timeout or the engine's exit ends it. Nothing here runs a command with the user's own token.
"""
from __future__ import annotations

import base64
import ctypes
import ctypes.wintypes as w
import json
import os
import re
import subprocess
import threading
import time
from pathlib import Path
from typing import Any

PREFIX = "AgentOrchestrator."
NETWORK_CAPABILITIES = ("S-1-15-3-1",)  # internetClient: outbound internet only (no LAN, no listening, never loopback)
WRITE_RIGHTS, READ_RIGHTS = "(OI)(CI)M", "(OI)(CI)RX"
SECRET_ENV = re.compile(r"KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|AUTH", re.I)
OUTPUT_LIMIT = 30_000


class SandboxError(RuntimeError):
    pass


# ---- Win32 plumbing

_k = ctypes.WinDLL("kernel32", use_last_error=True) if os.name == "nt" else None
_a = ctypes.WinDLL("advapi32", use_last_error=True) if os.name == "nt" else None
_u = ctypes.WinDLL("userenv", use_last_error=True) if os.name == "nt" else None


class SID_AND_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Sid", ctypes.c_void_p), ("Attributes", w.DWORD)]


class SECURITY_CAPABILITIES(ctypes.Structure):
    _fields_ = [("AppContainerSid", ctypes.c_void_p), ("Capabilities", ctypes.POINTER(SID_AND_ATTRIBUTES)),
                ("CapabilityCount", w.DWORD), ("Reserved", w.DWORD)]


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [("cb", w.DWORD), ("lpReserved", w.LPWSTR), ("lpDesktop", w.LPWSTR), ("lpTitle", w.LPWSTR),
                ("dwX", w.DWORD), ("dwY", w.DWORD), ("dwXSize", w.DWORD), ("dwYSize", w.DWORD),
                ("dwXCountChars", w.DWORD), ("dwYCountChars", w.DWORD), ("dwFillAttribute", w.DWORD), ("dwFlags", w.DWORD),
                ("wShowWindow", w.WORD), ("cbReserved2", w.WORD), ("lpReserved2", ctypes.c_void_p),
                ("hStdInput", w.HANDLE), ("hStdOutput", w.HANDLE), ("hStdError", w.HANDLE)]


class STARTUPINFOEXW(ctypes.Structure):
    _fields_ = [("StartupInfo", STARTUPINFOW), ("lpAttributeList", ctypes.c_void_p)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", w.HANDLE), ("hThread", w.HANDLE), ("dwProcessId", w.DWORD), ("dwThreadId", w.DWORD)]


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [(n, ctypes.c_ulonglong) for n in ("r", "w", "o", "rb", "wb", "ob")]


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [("PerProcessUserTimeLimit", ctypes.c_longlong), ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", w.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t), ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", w.DWORD), ("Affinity", ctypes.c_size_t), ("PriorityClass", w.DWORD),
                ("SchedulingClass", w.DWORD)]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION), ("IoInfo", IO_COUNTERS),
                ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]


if os.name == "nt":
    _k.CreateJobObjectW.restype = w.HANDLE
    _k.CreateJobObjectW.argtypes = [ctypes.c_void_p, w.LPCWSTR]
    _k.SetInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
    _k.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]
    _k.TerminateJobObject.argtypes = [w.HANDLE, w.UINT]
    _k.ResumeThread.argtypes = [w.HANDLE]
    _k.CloseHandle.argtypes = [w.HANDLE]
    _k.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
    _k.WaitForSingleObject.restype = w.DWORD
    _k.GetExitCodeProcess.argtypes = [w.HANDLE, ctypes.POINTER(w.DWORD)]
    _k.InitializeProcThreadAttributeList.argtypes = [ctypes.c_void_p, w.DWORD, w.DWORD, ctypes.POINTER(ctypes.c_size_t)]
    _k.UpdateProcThreadAttribute.argtypes = [ctypes.c_void_p, w.DWORD, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t,
                                             ctypes.c_void_p, ctypes.c_void_p]
    _k.DeleteProcThreadAttributeList.argtypes = [ctypes.c_void_p]
    _k.CreateProcessW.argtypes = [w.LPCWSTR, w.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, w.BOOL, w.DWORD, ctypes.c_void_p,
                                  w.LPCWSTR, ctypes.c_void_p, ctypes.POINTER(PROCESS_INFORMATION)]
    _u.CreateAppContainerProfile.argtypes = [w.LPCWSTR, w.LPCWSTR, w.LPCWSTR, ctypes.c_void_p, w.DWORD, ctypes.POINTER(ctypes.c_void_p)]
    _u.CreateAppContainerProfile.restype = ctypes.c_long
    _u.DeriveAppContainerSidFromAppContainerName.argtypes = [w.LPCWSTR, ctypes.POINTER(ctypes.c_void_p)]
    _u.DeriveAppContainerSidFromAppContainerName.restype = ctypes.c_long
    _u.DeleteAppContainerProfile.argtypes = [w.LPCWSTR]
    _u.DeleteAppContainerProfile.restype = ctypes.c_long
    _a.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(w.LPWSTR)]
    _a.ConvertStringSidToSidW.argtypes = [w.LPCWSTR, ctypes.POINTER(ctypes.c_void_p)]


def _check(ok: Any, what: str) -> None:
    if not ok:
        raise SandboxError(f"{what} failed (Windows error {ctypes.get_last_error()})")


CAPABILITY_NAME = "agentOrchestratorCommandSandbox"


def capability_sid() -> str:
    """The capability every container of this app carries (S-1-15-3-1024-...): what the one-time setup grants read access
    to (drive roots to list, tool folders), so only these containers gain it, not every AppContainer app."""
    kb = ctypes.WinDLL("kernelbase", use_last_error=True)
    kb.DeriveCapabilitySidsFromName.argtypes = [w.LPCWSTR, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)), ctypes.POINTER(w.DWORD),
                                                ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)), ctypes.POINTER(w.DWORD)]
    groups, group_count, caps, cap_count = ctypes.POINTER(ctypes.c_void_p)(), w.DWORD(), ctypes.POINTER(ctypes.c_void_p)(), w.DWORD()
    _check(kb.DeriveCapabilitySidsFromName(CAPABILITY_NAME, ctypes.byref(groups), ctypes.byref(group_count), ctypes.byref(caps),
                                           ctypes.byref(cap_count)) and cap_count.value, "DeriveCapabilitySidsFromName")
    text = w.LPWSTR()
    _check(_a.ConvertSidToStringSidW(caps[0], ctypes.byref(text)), "ConvertSidToStringSid")
    return text.value


def container_name(key: str) -> str:
    """An AppContainer profile name (at most 64 characters, letters, digits, dots, dashes) for a conversation key."""
    key = re.sub(r"[^A-Za-z0-9.-]", "", key)[:40] or "default"
    return PREFIX + key


def container_sid(name: str) -> tuple[ctypes.c_void_p, str]:
    """The container's SID (created on first use) as a pointer and as S-1-15-2-... text."""
    if os.name != "nt":
        raise SandboxError("The command sandbox needs Windows (AppContainer)")
    sid = ctypes.c_void_p()
    result = _u.CreateAppContainerProfile(name, name, "Agent Orchestrator command sandbox", None, 0, ctypes.byref(sid))
    if result == -2147024713:  # HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)
        result = _u.DeriveAppContainerSidFromAppContainerName(name, ctypes.byref(sid))
    if result != 0:
        raise SandboxError(f"Cannot create the AppContainer {name} (HRESULT {result & 0xFFFFFFFF:#x})")
    text = w.LPWSTR()
    _check(_a.ConvertSidToStringSidW(sid, ctypes.byref(text)), "ConvertSidToStringSid")
    return sid, text.value


def delete_container(name: str) -> None:
    if os.name == "nt":
        _u.DeleteAppContainerProfile(name)


# ---- folder grants: ACEs for the container's SID, kept equal to the wanted set (state file per container)

def _icacls(*args: str) -> None:
    result = subprocess.run(["icacls", *args, "/Q", "/C"], capture_output=True, text=True,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if result.returncode != 0:
        raise SandboxError(f"icacls {' '.join(args[:2])} failed: {(result.stdout + result.stderr).strip()[-400:]}")


def sync_grants(name: str, sid: str, wanted: dict[str, str], state_dir: Path) -> None:
    """Make the container's access exactly `wanted` ({folder: "write"|"read"}): grants it lacks are added (inheritable,
    so the folder's tree follows), ones no longer wanted are removed. A write folder's .git stays read-only (protect_git).
    Recorded in <state_dir>/<name>.json, so the next sync knows what to undo; the record is written before each change
    (the union of old and new while changing), so a crash never leaves an unrecorded grant."""
    state_dir.mkdir(parents=True, exist_ok=True)
    state_file = state_dir / f"{name}.json"
    try:
        state = json.loads(state_file.read_text(encoding="utf-8"))
        had, git = dict(state.get("grants", {})), dict(state.get("git", {}))
    except (OSError, ValueError, AttributeError):
        had, git = {}, {}
    want = {os.path.normcase(str(Path(p).resolve())): a for p, a in wanted.items()}

    def save(grants: dict[str, str]) -> None:
        state_file.write_text(json.dumps({"grants": grants, "git": git}), encoding="utf-8")

    save({**want, **had, **{p: "write" for p in want if want[p] == "write" or had.get(p) == "write"}})  # the widest, while changing
    for path, access in had.items():
        if want.get(path) != access:
            for dotgit in [g for g in git if Path(g).parent == Path(path)]:
                restore_git(dotgit, git.pop(dotgit))
                save({**want, **had})
            if Path(path).exists():
                _icacls(path, "/remove:g", f"*{sid}")
    for path, access in want.items():
        if had.get(path) != access:
            _icacls(path, "/grant", f"*{sid}:{WRITE_RIGHTS if access == 'write' else READ_RIGHTS}")
        for dotgit in find_git(path) if access == "write" else []:
            if dotgit not in git:
                git[dotgit] = str(state_dir / f"{name}.git-{len(git)}-{int(time.time() * 1000)}.acl")
                protect_git(dotgit, sid, git[dotgit], lambda: save({**want, **had}))
    save(want)


GIT_SKIP = {"node_modules", ".venv", "venv", "__pycache__", "bin", "obj", "dist", "build", "target", ".tox", ".mypy_cache"}


def find_git(folder: str, limit: int = 20000) -> list[str]:
    """Every .git (a repository folder, or the .git file of a worktree or submodule) in `folder`'s tree, nested ones
    included, skipping dependency and build folders; at most `limit` folders are looked at."""
    found, seen = [], 0
    for root, dirs, files in os.walk(folder):
        seen += 1
        if ".git" in dirs or ".git" in files:
            found.append(os.path.normcase(os.path.join(root, ".git")))
        dirs[:] = [d for d in dirs if d != ".git" and d.lower() not in GIT_SKIP]
        if seen >= limit:
            break
    return found


def unprotected_git(name: str, wanted: dict[str, str], state_dir: Path) -> list[str]:
    """The .git entries in the container's write folders that sync_grants has not protected yet (made by a command)."""
    try:
        protected = json.loads((state_dir / f"{name}.json").read_text(encoding="utf-8")).get("git", {})
    except (OSError, ValueError, AttributeError):
        protected = {}
    return [g for path, access in wanted.items() if access == "write" for g in find_git(path) if g not in protected]


RISKY_GIT_CONFIG = re.compile(r"(?im)^\s*(fsmonitor|hookspath|sshcommand|pager|editor|askpass|program|helper|textconv|external|"
                              r"clean|smudge|process|worktree)\s*=|^\s*\[\s*(include|includeif|alias|filter|credential|diff|merge|gpg)\b")


def risky_git(dotgit: str) -> str:
    """Why a .git a command made could run code as the user when git runs there (hooks, config that names programs, a
    .git file pointing elsewhere); "" when it holds nothing of that kind."""
    path = Path(dotgit)
    if path.is_file():
        return f"it is a .git file pointing to another repository ({path.read_text(encoding='utf-8', errors='replace').strip()[:200]})"
    hooks = [h.name for h in (path / "hooks").glob("*") if h.is_file() and not h.name.endswith(".sample")]
    if hooks:
        return f"it has hooks: {', '.join(hooks[:5])}"
    config = path / "config"
    match = RISKY_GIT_CONFIG.search(config.read_text(encoding="utf-8", errors="replace")) if config.is_file() else None
    return f"its config sets {match.group(0).strip()}" if match else ""


def quarantine_git(dotgit: str) -> str:
    """Renames a .git so git no longer uses it; the user can inspect it. Returns the new name."""
    target = f"{dotgit}.quarantined-{int(time.time())}"
    os.rename(dotgit, target)
    return target


def protect_git(dotgit: str, sid: str, saved: str, record: Any) -> None:
    """Keeps a granted folder's .git read-only for the container: hooks and config there run later as the user. Windows
    ignores deny entries for an AppContainer's own SID, so .git stops inheriting instead: its ACL is saved (to restore
    exactly), recorded, and the container's inherited write becomes read."""
    Path(saved).write_text(get_dacl(dotgit), encoding="utf-8")
    record()  # recorded before the first change, so a crash still restores it
    _icacls(dotgit, "/inheritance:d")
    _icacls(dotgit, "/remove:g", f"*{sid}")
    _icacls(dotgit, "/grant", f"*{sid}:{READ_RIGHTS if Path(dotgit).is_dir() else '(RX)'}")  # a worktree's .git is a file


def restore_git(dotgit: str, saved: str) -> None:
    """Puts back the ACL protect_git saved (inheritance included); the .git tree then inherits as before."""
    if Path(dotgit).exists() and Path(saved).is_file():
        restore_dacl(dotgit, Path(saved).read_text(encoding="utf-8"))
    Path(saved).unlink(missing_ok=True)


LIST_RIGHTS = 0x001000A1  # FILE_LIST_DIRECTORY | FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE: names only, no contents


_adv = ctypes.WinDLL("advapi32", use_last_error=True) if os.name == "nt" else None
if os.name == "nt":
    _adv.GetFileSecurityW.argtypes = [w.LPCWSTR, w.DWORD, ctypes.c_void_p, w.DWORD, ctypes.POINTER(w.DWORD)]
    _adv.SetFileSecurityW.argtypes = [w.LPCWSTR, w.DWORD, ctypes.c_void_p]
    _adv.ConvertSecurityDescriptorToStringSecurityDescriptorW.argtypes = [ctypes.c_void_p, w.DWORD, w.DWORD,
                                                                          ctypes.POINTER(w.LPWSTR), ctypes.c_void_p]
    _adv.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [w.LPCWSTR, w.DWORD, ctypes.POINTER(ctypes.c_void_p),
                                                                          ctypes.c_void_p]
    _adv.GetSecurityDescriptorDacl.argtypes = [ctypes.c_void_p, ctypes.POINTER(w.BOOL), ctypes.POINTER(ctypes.c_void_p),
                                               ctypes.POINTER(w.BOOL)]
    _adv.SetNamedSecurityInfoW.argtypes = [w.LPWSTR, ctypes.c_int, w.DWORD, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                           ctypes.c_void_p]


def get_dacl(path: str) -> str:
    """The DACL of a file or folder as SDDL ("D:..."), read with GetFileSecurity."""
    needed = w.DWORD()
    _adv.GetFileSecurityW(path, 4, None, 0, ctypes.byref(needed))  # DACL_SECURITY_INFORMATION
    buffer = ctypes.create_string_buffer(needed.value)
    _check(_adv.GetFileSecurityW(path, 4, buffer, needed, ctypes.byref(needed)), f"GetFileSecurity({path})")
    text = w.LPWSTR()
    _check(_adv.ConvertSecurityDescriptorToStringSecurityDescriptorW(buffer, 1, 4, ctypes.byref(text), None), "SD to SDDL")
    return text.value


def _descriptor(sddl: str) -> ctypes.c_void_p:
    descriptor = ctypes.c_void_p()
    _check(_adv.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(descriptor), None), "SDDL to SD")
    return descriptor


def set_folder_ace(path: str, sid: str, rights: int | None) -> None:
    """Puts one non-inheritable allow ACE for `sid` on the folder itself (rights None: removes it) with SetFileSecurity,
    which, unlike icacls or SetNamedSecurityInfo, does not walk the subtree: safe on a drive root or a profile folder."""
    sddl = re.sub(rf"\(A;[^;()]*;[^;()]*;;;{re.escape(sid)}\)", "", get_dacl(path), flags=re.I)  # our earlier ACE, if any
    if rights is not None:
        ace = f"(A;;0x{rights:x};;;{sid})"
        head, aces = re.match(r"(D:[A-Z]*)(.*)", sddl, re.S).groups()
        inherited = re.search(r"\([A-Z]+;[^;()]*ID[^;()]*;", aces)  # explicit ACEs come first: insert before the inherited
        sddl = head + (aces[:inherited.start()] + ace + aces[inherited.start():] if inherited else aces + ace)
    _check(_adv.SetFileSecurityW(path, 4, _descriptor(sddl)), f"SetFileSecurity({path})")


def restore_dacl(path: str, sddl: str) -> None:
    """Puts back a DACL get_dacl read, inheritance included: its explicit ACEs, and either the parent's inheritable ones
    again (not protected) or none (protected). SetNamedSecurityInfo propagates it to the folder's tree."""
    head, aces = re.match(r"(D:[A-Z]*)(.*)", sddl, re.S).groups()
    explicit = "".join(ace for ace in re.findall(r"\([^()]*\)", aces) if "ID" not in ace.split(";")[1])
    present, defaulted, dacl = w.BOOL(), w.BOOL(), ctypes.c_void_p()
    descriptor = _descriptor("D:" + explicit)
    _check(_adv.GetSecurityDescriptorDacl(descriptor, ctypes.byref(present), ctypes.byref(dacl), ctypes.byref(defaulted)), "GetSecurityDescriptorDacl")
    flags = 4 | (0x80000000 if "P" in head[2:] else 0x20000000)  # DACL + PROTECTED or UNPROTECTED
    result = _adv.SetNamedSecurityInfoW(ctypes.create_unicode_buffer(path), 1, flags, None, None, dacl, None)
    if result != 0:
        raise SandboxError(f"SetNamedSecurityInfo({path}) failed (Windows error {result})")


def list_ancestors(folder: str) -> None:
    """Lets this app's containers list the folders above `folder` (names only, those the user may change; never the
    drive root): PowerShell will not start inside a folder whose ancestors it cannot list."""
    cap = capability_sid()
    for parent in Path(folder).resolve().parents:
        if parent.parent == parent:
            break  # the drive root: the administrator setup's
        try:
            set_folder_ace(str(parent), cap, LIST_RIGHTS)
        except SandboxError:
            pass  # not the user's (C:\Users): the setup covers it


TOOL_PROGRAMS = ("python", "py", "node", "npm", "git", "dotnet", "java", "go", "cargo", "rustc", "pwsh", "uv", "deno", "bun")


def tool_folders() -> list[str]:
    """Install folders of the developer tools on PATH (outside Windows' own folder), which a container cannot read unless
    granted: a tool's folder, and its parent when the program sits in bin, cmd or Scripts."""
    import shutil
    windows = os.path.normcase(os.environ.get("SystemRoot", r"C:\Windows"))
    profile = Path.home().resolve()
    found: list[str] = []
    for program in TOOL_PROGRAMS:
        exe = shutil.which(program)
        if not exe or os.path.normcase(exe).startswith(windows) or "windowsapps" in exe.lower():
            continue
        folder = Path(exe).resolve().parent
        # the parent of a bin folder is the tool's own install, except inside the profile (~/.local holds other data)
        whole = folder.name.lower() in ("bin", "cmd", "scripts") and profile not in folder.parents
        for candidate in (folder, folder.parent) if whole else (folder,):
            if str(candidate) not in found and candidate.parent != candidate:
                found.append(str(candidate))
    return found


def fixed_drives() -> list[str]:
    mask, drives = _k.GetLogicalDrives(), []
    _k.GetDriveTypeW.argtypes = [w.LPCWSTR]
    for i in range(26):
        root = f"{chr(65 + i)}:\\"
        if mask >> i & 1 and _k.GetDriveTypeW(root) == 3:  # DRIVE_FIXED
            drives.append(root)
    return drives


def setup(state_dir: Path, undo: bool = False) -> list[str]:
    """The one-time administrator step: this app's containers (their capability, not every AppContainer app) may list
    each fixed drive's root and the Users folder, and read the developer tools' folders. It does not close
    HKCU\\Environment as well: environment_key, run without elevation, since an administrator's HKCU may not be the
    user's. Returns what it did."""
    cap, done = capability_sid(), []
    users = str(Path(os.environ.get("SystemDrive", "C:") + "\\Users"))
    for folder in [*fixed_drives(), users]:
        if not has_acls(folder):
            done.append(f"skipped {folder}: its file system (FAT32, exFAT) has no permissions, and the sandbox cannot open it")
            continue
        try:
            set_folder_ace(folder, cap, None if undo else LIST_RIGHTS)
            done.append(f"{'unlisted' if undo else 'list'}: {folder}")
        except SandboxError as exc:
            done.append(f"FAILED {folder}: {exc}")
    for folder in tool_folders():
        try:
            _icacls(folder, "/remove:g", f"*{cap}") if undo else _icacls(folder, "/grant", f"*{cap}:{READ_RIGHTS}")
            done.append(f"{'unread' if undo else 'read'}: {folder}")
        except SandboxError as exc:
            done.append(f"FAILED {folder}: {exc}")
    return done


ENVIRONMENT_KEY = r"HKCU:\Environment"


def environment_key(undo: bool, state_dir: Path, key: str = ENVIRONMENT_KEY) -> str:
    """Windows lets every AppContainer read HKCU\\Environment, where user environment variables (API keys among them)
    live: the setup removes the AppContainer entries from that key's ACL (saved to restore on undo). The rest of HKCU
    stays readable to AppContainers, as Windows ships it."""
    saved = state_dir / f"{re.sub(r'[^A-Za-z0-9]+', '-', key).strip('-').lower()}.sddl"
    quoted = str(saved).replace("'", "''")  # inside PowerShell single quotes
    if not undo and key == ENVIRONMENT_KEY and not environment_secrets() and not saved.is_file():
        return f"{key}: no secret-looking variables, left readable (PowerShell needs it)"
    if undo:
        if not saved.is_file():
            return f"{key}: nothing to restore"
        script = (f"$a = Get-Acl '{key}'; $a.SetSecurityDescriptorSddlForm((Get-Content -Raw -LiteralPath '{quoted}').Trim(), "
                  f"'Access'); Set-Acl '{key}' $a")
    else:
        script = (f"$a = Get-Acl '{key}'; if (-not (Test-Path -LiteralPath '{quoted}')) {{ Set-Content -LiteralPath '{quoted}' "
                  f"$a.Sddl }}; $a.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) | Where-Object {{ "
                  f"$_.IdentityReference.Value -like 'S-1-15-*' }} | ForEach-Object {{ [void]$a.RemoveAccessRuleSpecific($_) }}; Set-Acl '{key}' $a")
    state_dir.mkdir(parents=True, exist_ok=True)
    result = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True, text=True,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if result.returncode != 0 or result.stderr.strip():
        return f"FAILED {key}: {(result.stderr or result.stdout).strip()[-300:]}"
    if undo:
        saved.unlink(missing_ok=True)
    return f"{key}: {'AppContainer read restored' if undo else 'no longer readable by AppContainers'}"


def has_acls(folder: str) -> bool:
    """Whether the folder's volume keeps permissions (NTFS, ReFS); FAT32 and exFAT do not."""
    flags = w.DWORD()
    root = os.path.splitdrive(os.path.abspath(folder))[0] + "\\"
    return bool(_k.GetVolumeInformationW(root, None, 0, None, None, ctypes.byref(flags), None, 0)) and bool(flags.value & 0x8)


def environment_secrets() -> list[str]:
    """Names of the secret-looking user environment variables (HKCU\\Environment), which a container could read
    unless environment_key closed the key."""
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
        names = [winreg.EnumValue(key, i)[0] for i in range(winreg.QueryInfoKey(key)[1])]
    return [name for name in names if SECRET_ENV.search(name)]


def environment_protected() -> bool:
    """Whether HKCU\\Environment has no AppContainer entry left (environment_key ran), read with RegGetKeySecurity."""
    import winreg
    _adv.RegGetKeySecurity.argtypes = [w.HANDLE, w.DWORD, ctypes.c_void_p, ctypes.POINTER(w.DWORD)]
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment", 0, 0x00020000) as key:  # READ_CONTROL
        needed = w.DWORD(0)
        _adv.RegGetKeySecurity(w.HANDLE(key.handle), 4, None, ctypes.byref(needed))
        buffer = ctypes.create_string_buffer(needed.value)
        result = _adv.RegGetKeySecurity(w.HANDLE(key.handle), 4, buffer, ctypes.byref(needed))
    if result != 0:
        raise SandboxError(f"RegGetKeySecurity failed (Windows error {result})")
    text = w.LPWSTR()
    _check(_adv.ConvertSecurityDescriptorToStringSecurityDescriptorW(buffer, 1, 4, ctypes.byref(text), None), "SD to SDDL")
    return not re.search(r";;;(AC|S-1-15-[^)]*)\)", text.value)


def final_path(path: str) -> str:
    """The real path of an existing folder after every link, junction and short name (GetFinalPathNameByHandle), as a
    drive-letter path; raises for a network (UNC) target."""
    _k.CreateFileW.restype = w.HANDLE
    _k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
    _k.GetFinalPathNameByHandleW.argtypes = [w.HANDLE, w.LPWSTR, w.DWORD, w.DWORD]
    handle = _k.CreateFileW(path, 0x80, 7, None, 3, 0x02000000, None)  # FILE_READ_ATTRIBUTES, share all, OPEN_EXISTING, BACKUP_SEMANTICS
    if handle in (None, w.HANDLE(-1).value):
        raise SandboxError(f"cannot open {path} (Windows error {ctypes.get_last_error()})")
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        _check(_k.GetFinalPathNameByHandleW(handle, buffer, 32768, 0), "GetFinalPathNameByHandle")
    finally:
        _k.CloseHandle(handle)
    real = buffer.value
    if real.upper().startswith("\\\\?\\UNC\\") or not re.match(r"\\\\\?\\[A-Za-z]:\\", real):
        raise SandboxError(f"{path} is not on a local drive")
    return real[4:]


def secret_values(extra: list[str] = ()) -> list[str]:
    """Values that must never reach the model through a command's output: secret-named variables of this process and of
    HKCU\\Environment, plus `extra`. At least 8 characters, longest first."""
    values = {v for k, v in os.environ.items() if SECRET_ENV.search(k)} | set(extra)
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
            for i in range(winreg.QueryInfoKey(key)[1]):
                name, value, _ = winreg.EnumValue(key, i)
                if SECRET_ENV.search(name) and isinstance(value, str):
                    values.add(value)
    except OSError:
        pass
    return sorted((v.strip() for v in values if len(v.strip()) >= 8), key=len, reverse=True)


def redact(text: str, secrets: list[str]) -> str:
    for value in secrets:
        text = text.replace(value, "[secret removed]")
    return text


def setup_done() -> bool:
    """Whether the system drive's root lists for this app's containers (setup ran), so PowerShell can work."""
    root = os.environ.get("SystemDrive", "C:") + "\\"
    try:
        result = subprocess.run(["icacls", root], capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except OSError:
        return False
    return capability_sid() in result.stdout


def revoke_all(name: str, state_dir: Path) -> None:
    """Undo every grant recorded for the container and delete its profile."""
    try:
        _, sid = container_sid(name)
        sync_grants(name, sid, {}, state_dir)
    finally:
        delete_container(name)
        (state_dir / f"{name}.json").unlink(missing_ok=True)


# ---- running one command

def clean_env(temp: Path) -> dict[str, str]:
    """The command's environment: the host's without secrets, with temp and tool caches inside the sandbox's own folder."""
    env = {k: v for k, v in os.environ.items() if not SECRET_ENV.search(k)}
    for key in ("TEMP", "TMP"):
        env[key] = str(temp)
    env.update({"NPM_CONFIG_CACHE": str(temp / "npm-cache"), "PIP_CACHE_DIR": str(temp / "pip-cache"),
                "NUGET_PACKAGES": str(temp / "nuget"), "DOTNET_CLI_HOME": str(temp), "PYTHONPYCACHEPREFIX": str(temp / "pycache"),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "GIT_CONFIG_NOSYSTEM": "1",
                "GIT_CONFIG_GLOBAL": str(temp / "gitconfig")})  # ~/.gitconfig is out of reach: no warning on every git call
    return env


def shell_command(command: str, shell: str) -> str:
    root = os.environ.get("SystemRoot", r"C:\Windows")
    if shell == "cmd":
        return f'"{root}\\System32\\cmd.exe" /d /s /c "{command}"'
    # Plain text out: no progress records, and every stream (errors included) as text instead of CLIXML; the exit code
    # is the last native command's, else 1 when the command failed, else 0.
    # A non-terminating error (a refused write, say) also fails the command, so the model and the sandbox note see it.
    script = ("$ProgressPreference = 'SilentlyContinue'\n$global:LASTEXITCODE = 0\n$Error.Clear()\n"
              f"try {{ & {{\n{command}\n}} *>&1 | Out-String -Stream -Width 250; $ok = $? }} catch {{ $_ | Out-String; $ok = $false }}\n"
              "if ($global:LASTEXITCODE) { exit $global:LASTEXITCODE } elseif (-not $ok -or $Error.Count) { exit 1 } else { exit 0 }")
    encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    return (f'"{root}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoLogo -NoProfile -NonInteractive '
            f"-ExecutionPolicy Bypass -EncodedCommand {encoded}")


def run(name: str, command: str, cwd: str, temp: Path, *, shell: str = "powershell", network: bool = False,
        timeout: float = 120.0) -> tuple[int | None, str]:
    """Runs `command` in the AppContainer `name` (its grants already synced) with cwd `cwd`; returns (exit code, or
    None on timeout, combined output clipped to OUTPUT_LIMIT). `network` adds the network capabilities for this command."""
    sid, _ = container_sid(name)
    temp.mkdir(parents=True, exist_ok=True)
    wanted = [capability_sid(), *(NETWORK_CAPABILITIES if network else ())]
    caps = (SID_AND_ATTRIBUTES * len(wanted))()
    for i, text in enumerate(wanted):
        cap = ctypes.c_void_p()
        _check(_a.ConvertStringSidToSidW(text, ctypes.byref(cap)), "ConvertStringSidToSid")
        caps[i] = SID_AND_ATTRIBUTES(cap, 0x4)  # SE_GROUP_ENABLED
    security = SECURITY_CAPABILITIES(sid, caps, len(wanted), 0)

    read_fd, write_fd = os.pipe()
    import msvcrt
    out_handle = msvcrt.get_osfhandle(write_fd)
    os.set_handle_inheritable(out_handle, True)
    null = open(os.devnull, "rb")
    in_handle = msvcrt.get_osfhandle(null.fileno())
    os.set_handle_inheritable(in_handle, True)
    handles = (w.HANDLE * 2)(in_handle, out_handle)

    size = ctypes.c_size_t()
    _k.InitializeProcThreadAttributeList(None, 2, 0, ctypes.byref(size))
    attributes = ctypes.create_string_buffer(size.value)
    _check(_k.InitializeProcThreadAttributeList(attributes, 2, 0, ctypes.byref(size)), "InitializeProcThreadAttributeList")
    job = info = None
    try:
        _check(_k.UpdateProcThreadAttribute(attributes, 0, 0x00020009, ctypes.byref(security), ctypes.sizeof(security), None, None),
               "UpdateProcThreadAttribute(security capabilities)")
        _check(_k.UpdateProcThreadAttribute(attributes, 0, 0x00020002, handles, ctypes.sizeof(handles), None, None),
               "UpdateProcThreadAttribute(handle list)")
        startup = STARTUPINFOEXW()
        startup.StartupInfo.cb = ctypes.sizeof(startup)
        startup.StartupInfo.dwFlags = 0x100  # STARTF_USESTDHANDLES
        startup.StartupInfo.hStdInput, startup.StartupInfo.hStdOutput, startup.StartupInfo.hStdError = in_handle, out_handle, out_handle
        startup.lpAttributeList = ctypes.cast(attributes, ctypes.c_void_p)
        env = clean_env(temp)
        block = ctypes.create_unicode_buffer("".join(f"{k}={v}\0" for k, v in sorted(env.items())) + "\0")
        job = _k.CreateJobObjectW(None, None)
        _check(job, "CreateJobObject")
        limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
        limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x400  # KILL_ON_JOB_CLOSE, DIE_ON_UNHANDLED_EXCEPTION
        _check(_k.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)), "SetInformationJobObject")
        info = PROCESS_INFORMATION()
        flags = 0x00080000 | 0x00000004 | 0x00000400 | 0x08000000  # EXTENDED_STARTUPINFO, SUSPENDED, UNICODE_ENVIRONMENT, NO_WINDOW
        _check(_k.CreateProcessW(None, ctypes.create_unicode_buffer(shell_command(command, shell)), None, None, True, flags,
                                 block, cwd, ctypes.byref(startup), ctypes.byref(info)), "CreateProcess in the AppContainer")
        _check(_k.AssignProcessToJobObject(job, info.hProcess), "AssignProcessToJobObject")
        _k.ResumeThread(info.hThread)
        os.close(write_fd)
        write_fd = -1
        chunks: list[bytes] = []
        reader = threading.Thread(target=lambda: chunks.extend(iter(lambda: os.read(read_fd, 65536), b"")), daemon=True)
        reader.start()
        finished = _k.WaitForSingleObject(info.hProcess, int(timeout * 1000)) == 0
        _k.TerminateJobObject(job, 1)  # the command's children too, finished or not
        reader.join(5)
        code = w.DWORD()
        _k.GetExitCodeProcess(info.hProcess, ctypes.byref(code))
        output = b"".join(chunks).decode("utf-8", errors="replace")
        if len(output) > OUTPUT_LIMIT:
            output = output[:OUTPUT_LIMIT // 3] + f"\n... {len(output) - OUTPUT_LIMIT} characters cut ...\n" + output[-2 * OUTPUT_LIMIT // 3:]
        return (code.value if finished else None), output
    finally:
        _k.DeleteProcThreadAttributeList(attributes)
        for handle in (info.hThread if info else None, info.hProcess if info else None, job):
            if handle:
                _k.CloseHandle(handle)
        if write_fd >= 0:
            os.close(write_fd)
        os.close(read_fd)
        null.close()


# Output that suggests the sandbox stopped the command: network calls (asked about once), and file access too (a note
# to the model, which can then request the folders).
NETWORK_BLOCKED = re.compile(r"getaddrinfo|ENOTFOUND|EAI_AGAIN|No such host is known|WinError 10013|WSAEACCES|"
                             r"forbidden by its access permissions|Unable to connect to the remote server|network is unreachable|"
                             r"remote name could not be resolved|could not resolve host|Temporary failure in name resolution|"
                             r"NameResolutionError|Failed to establish a new connection|ECONNREFUSED", re.I)
BLOCKED = re.compile(r"access (is )?denied|permission denied|PermissionError|EACCES|EPERM|UnauthorizedAccess|"
                     r"not have permission|WinError 5\b|" + NETWORK_BLOCKED.pattern, re.I)


if __name__ == "__main__":  # the launcher's "Set up sandbox access" runs this elevated
    import argparse
    parser = argparse.ArgumentParser(description="One-time administrator setup of the command sandbox")
    parser.add_argument("--setup", action="store_true", help="Let this app's containers list drive roots and read tool folders")
    parser.add_argument("--undo-setup", action="store_true", help="Remove what --setup added")
    parser.add_argument("--protect-environment", action="store_true", help="Close HKCU Environment to AppContainers (no elevation)")
    parser.add_argument("--unprotect-environment", action="store_true", help="Undo --protect-environment")
    parser.add_argument("--log", help="Write the report to this file")
    parser.add_argument("--state", default=str(Path(os.environ.get("APPDATA", ".")) / "Agent Orchestrator" / "sandbox"),
                        help="Where the setup keeps what it needs to undo")
    args = parser.parse_args()
    if args.protect_environment or args.unprotect_environment:
        lines = [environment_key(args.unprotect_environment, Path(args.state))]
    else:
        lines = setup(Path(args.state), undo=args.undo_setup) if args.setup or args.undo_setup else [parser.format_help()]
    report = "\n".join(lines)
    if args.log:
        Path(args.log).write_text(report, encoding="utf-8")
    print(report)
    raise SystemExit(1 if any(line.startswith("FAILED") for line in lines) else 0)
