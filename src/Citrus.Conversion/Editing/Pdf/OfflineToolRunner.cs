using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Citrus.Conversion.Editing;

namespace Citrus.Conversion;

public sealed record OfflineToolResult(int ExitCode,string Log);

/// <summary>Windows AppContainer with zero capabilities, private writable staging and a kill-on-close Job Object. There is no unrestricted fallback.</summary>
public static class OfflineToolRunner
{
    // ACL edits on the shared read-only bundle must not race with another lease.
    private static readonly SemaphoreSlim LeaseGate=new(1,1);
    public static async Task<OfflineToolResult> RunAsync(string executable,IReadOnlyList<string> arguments,string readOnlyRoot,string staging,TimeSpan timeout,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Offline document workers require Windows AppContainer isolation.");
        ct.ThrowIfCancellationRequested();await LeaseGate.WaitAsync(ct).ConfigureAwait(false);
        try{return await Task.Run(()=>
        {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Offline document workers require Windows AppContainer isolation.");
            return RunWindowsAsync(executable,arguments,readOnlyRoot,staging,timeout,ct);
        },ct).ConfigureAwait(false);}
        finally{LeaseGate.Release();}
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<OfflineToolResult> RunWindowsAsync(string executable,IReadOnlyList<string> arguments,string readOnlyRoot,string staging,TimeSpan timeout,CancellationToken ct)
    {
        string bundle=Path.GetFullPath(readOnlyRoot),work=Path.GetFullPath(staging),exe=Path.GetFullPath(executable);
        if(!exe.StartsWith(bundle+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(exe)||!Directory.Exists(work))throw new ArgumentException("The worker must be inside the admitted offline bundle with existing private staging.");
        string program=Path.Combine(bundle,"libreoffice","program"),pythonHome=Path.Combine(program,"python-core-3.13.15");
        bool bundledPython=string.Equals(exe,Path.Combine(program,"python.exe"),StringComparison.OrdinalIgnoreCase);
        string application=exe,argumentZero=exe;
        if(bundledPython)
        {
            // LibreOffice's launcher starts another process without inheriting
            // our standard handles. Start its pinned interpreter directly,
            // preserving the vendor's Python-home argv[0] contract instead.
            application=Path.Combine(pythonHome,"bin","python.exe");argumentZero=pythonHome;
            if(!File.Exists(application))throw new NotSupportedException("The bundled Python interpreter is missing. Repair the offline feature pack.");
        }
        string name="Citrus.Offline."+Guid.NewGuid().ToString("N");nint sid=0,attributes=0,caps=0,handles=0,environment=0;
        SecurityIdentifier? identity=null;bool bundleGranted=false,workGranted=false;
        using var job=CreateJobObject(0,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new ExtendedLimits { Basic=new BasicLimits { LimitFlags=0x2000|0x200|0x8,ActiveProcessLimit=16 },JobMemoryLimit=(nuint)(2L*1024*1024*1024) };
        if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()))throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Marshal.ThrowExceptionForHR(CreateAppContainerProfile(name,name,"Isolated offline document conversion",0,0,out sid));identity=new(sid);
            Grant(bundle,identity,FileSystemRights.ReadAndExecute);bundleGranted=true;Grant(work,identity,FileSystemRights.FullControl);workGranted=true;
            string log=Path.Combine(work,"worker-"+Guid.NewGuid().ToString("N")+".log");
            // A concurrent host launch can briefly inherit a duplicate. Owned
            // log deletion must remain possible after this worker tree retires.
            using var sink=File.OpenHandle(log,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
            using var input=CreateFile("NUL",0x80000000,3,0,3,0,0);if(input.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            nuint length=0;InitializeProcThreadAttributeList(0,2,0,ref length);attributes=Marshal.AllocHGlobal((nint)length);
            if(!InitializeProcThreadAttributeList(attributes,2,0,ref length))throw new Win32Exception(Marshal.GetLastWin32Error());
            caps=Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid=sid },caps,false);
            if(!UpdateProcThreadAttribute(attributes,0,0x20009,caps,(nuint)Marshal.SizeOf<SecurityCapabilities>(),0,0))throw new Win32Exception(Marshal.GetLastWin32Error());
            handles=Marshal.AllocHGlobal(nint.Size*2);Marshal.WriteIntPtr(handles,0,sink.DangerousGetHandle());Marshal.WriteIntPtr(handles,nint.Size,input.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(attributes,0,0x20002,handles,(nuint)(nint.Size*2),0,0))throw new Win32Exception(Marshal.GetLastWin32Error());
            var start=new StartupInfoEx { Info=new StartupInfo { Size=(uint)Marshal.SizeOf<StartupInfoEx>(),Flags=0x100,StdInput=input.DangerousGetHandle(),StdOutput=sink.DangerousGetHandle(),StdError=sink.DangerousGetHandle() },Attributes=attributes };
            // Documents are untrusted: never forward host credentials or a developer's
            // environment. Every non-system directory belongs to this private job.
            string profile=Path.Combine(work,"worker-profile"),roaming=Path.Combine(profile,"AppData","Roaming"),local=Path.Combine(profile,"AppData","Local");
            Directory.CreateDirectory(roaming);Directory.CreateDirectory(local);
            string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var variables=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SYSTEMROOT"]=windows,["WINDIR"]=windows,["COMSPEC"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),
                ["USERPROFILE"]=profile,["HOME"]=profile,["APPDATA"]=roaming,["LOCALAPPDATA"]=local,["TEMP"]=work,["TMP"]=work,
                ["SAL_USE_VCLPLUGIN"]="svp",["SAL_DISABLE_OPENCL"]="1"
            };
            variables["PYTHONHOME"]=pythonHome;variables["PYTHONPATH"]=program;variables["PYTHONNOUSERSITE"]="1";variables["PYTHONDONTWRITEBYTECODE"]="1";
            if(bundledPython)
            {
                variables["UNO_PATH"]=program;
                variables["URE_BOOTSTRAP"]="vnd.sun.star.pathname:"+Path.Combine(program,"fundamental.ini");
                variables["PYTHONPATH"]=string.Join(';',program,Path.Combine(pythonHome,"lib"),Path.Combine(pythonHome,"lib","site-packages"));
            }
            variables["PATH"]=program+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
            environment=Marshal.StringToHGlobalUni(string.Join('\0',variables.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"="+p.Value))+"\0\0");
            var command=new StringBuilder(string.Join(' ',new[]{argumentZero}.Concat(arguments).Select(Quote)));
            ProcessInformation process=default;bool started=false;int launchError=0,inheritError=0;
            try
            {
                // The explicit child handle list needs inheritable handles, but
                // unrelated Process.Start calls must not inherit them while we wait.
                if(!SetHandleInformation(sink.DangerousGetHandle(),1,1)||!SetHandleInformation(input.DangerousGetHandle(),1,1))throw new Win32Exception(Marshal.GetLastWin32Error());
                started=CreateProcess(application,command,0,0,true,0x80000|0x400|0x8000000|4,environment,work,ref start,out process);
                if(!started)launchError=Marshal.GetLastWin32Error();
            }
            finally
            {
                if(!SetHandleInformation(sink.DangerousGetHandle(),1,0))inheritError=Marshal.GetLastWin32Error();
                if(!SetHandleInformation(input.DangerousGetHandle(),1,0)&&inheritError==0)inheritError=Marshal.GetLastWin32Error();
            }
            if(!started)throw new Win32Exception(launchError,"Could not start the isolated document worker.");
            using var processHandle=new SafeFileHandle(process.Process,true);using var thread=new SafeFileHandle(process.Thread,true);
            if(inheritError!=0){TerminateProcess(processHandle,1);throw new Win32Exception(inheritError,"Could not retire the worker's temporary handle inheritance.");}
            if(!AssignProcessToJobObject(job,processHandle)){TerminateProcess(processHandle,1);throw new Win32Exception(Marshal.GetLastWin32Error());}
            if(ResumeThread(thread)==uint.MaxValue){TerminateJobObject(job,1);throw new Win32Exception(Marshal.GetLastWin32Error());}
            using var timeoutSource=new CancellationTokenSource(timeout);using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeoutSource.Token);
            try
            {
                using var running=Process.GetProcessById((int)process.ProcessId);await running.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                int code=running.ExitCode;TerminateJobObject(job,0);await WaitForEmptyAsync(job).ConfigureAwait(false);
                sink.Dispose();string text=await ReadLogAsync(log,ct).ConfigureAwait(false);
                return new(code,text);
            }
            catch(OperationCanceledException)when(!ct.IsCancellationRequested)
            {
                TerminateJobObject(job,1);await WaitForEmptyAsync(job).ConfigureAwait(false);sink.Dispose();
                throw new TimeoutException("The isolated document conversion exceeded its time limit. "+await ReadLogAsync(log,CancellationToken.None).ConfigureAwait(false));
            }
            finally{TerminateJobObject(job,1);await WaitForEmptyAsync(job).ConfigureAwait(false);}
        }
        finally
        {
            if(attributes!=0){DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);}if(caps!=0)Marshal.FreeHGlobal(caps);if(handles!=0)Marshal.FreeHGlobal(handles);if(environment!=0)Marshal.FreeHGlobal(environment);
            if(identity is not null){if(workGranted)Revoke(work,identity);if(bundleGranted)Revoke(bundle,identity);}if(sid!=0){FreeSid(sid);DeleteAppContainerProfile(name);}
        }
    }
    private static async Task<string> ReadLogAsync(string path,CancellationToken ct)
    {
        SafeEditorFiles.RejectLinks(path);if(!File.Exists(path))return "";
        await using var data=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var reader=new StreamReader(data);
        char[] buffer=new char[64*1024];int read=await reader.ReadBlockAsync(buffer,ct).ConfigureAwait(false);return new string(buffer,0,read);
    }
    private static async Task WaitForEmptyAsync(SafeFileHandle job)
    {
        for(int attempt=0;attempt<100;attempt++)
        {if(!QueryInformationJobObject(job,1,out var accounting,(uint)Marshal.SizeOf<Accounting>(),0)||accounting.ActiveProcesses==0)return;await Task.Delay(10).ConfigureAwait(false);}
        throw new IOException("The isolated worker process tree did not retire.");
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Grant(string directory,SecurityIdentifier sid,FileSystemRights rights)
    {var info=new DirectoryInfo(directory);var acl=info.GetAccessControl();acl.AddAccessRule(new(sid,rights,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));info.SetAccessControl(acl);}
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Revoke(string directory,SecurityIdentifier sid)
    {if(!Directory.Exists(directory))return;var info=new DirectoryInfo(directory);var acl=info.GetAccessControl();acl.PurgeAccessRules(sid);info.SetAccessControl(acl);}
    private static string Quote(string value)
    {
        var text=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')text.Append('\\',slashes*2+1).Append('"');else text.Append('\\',slashes).Append(c);slashes=0;}
        return text.Append('\\',slashes*2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)]private struct SecurityCapabilities{public nint AppContainerSid,Capabilities;public uint CapabilityCount,Reserved;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo{public uint Size;public nint Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;public ushort Show,ReservedSize;public nint ReservedData,StdInput,StdOutput,StdError;}
    [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx{public StartupInfo Info;public nint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation{public nint Process,Thread;public uint ProcessId,ThreadId;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint LimitFlags;public nuint MinWorkingSet,MaxWorkingSet;public uint ActiveProcessLimit;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential)]private struct Accounting{public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;}
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]private static extern int CreateAppContainerProfile(string name,string display,string description,nint capabilities,uint count,out nint sid);
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")]private static extern nint FreeSid(nint sid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeFileHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateJobObject(SafeFileHandle job,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,out Accounting info,uint size,nint returnedLength);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool InitializeProcThreadAttributeList(nint attributes,int count,uint flags,ref nuint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool UpdateProcThreadAttribute(nint attributes,uint flags,nuint attribute,nint value,nuint size,nint previous,nint returnedSize);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateProcessW")]private static extern bool CreateProcess(string application,StringBuilder command,nint processAttributes,nint threadAttributes,bool inherit,uint flags,nint environment,string directory,ref StartupInfoEx startup,out ProcessInformation information);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateProcess(SafeFileHandle process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetHandleInformation(nint handle,uint mask,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint creation,uint flags,nint template);
}
