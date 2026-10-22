"""Trusted LibreOfficeKit worker inside the host's zero-capability AppContainer.

Windows OSL UNO pipes use a hard-coded non-LOCAL namespace. Private configuration
disables macros, OLE/DDE and link updates before the isolated library opens a document.
"""
import json
import pathlib
import ctypes
import os
import sys
import traceback


def main():
    request_path = pathlib.Path(sys.argv[1]).resolve()
    request = json.loads(request_path.read_text(encoding="utf-8-sig"))
    output = pathlib.Path(request["OutputDirectory"]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = pathlib.Path(request["Input"]).resolve()
    profile = request_path.parent / "office-profile"
    profile_user = profile / "user"
    profile_user.mkdir(parents=True, exist_ok=True)
    (profile_user / "registrymodifications.xcu").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<oor:items xmlns:oor="http://openoffice.org/2001/registry">'
        '<item oor:path="/org.openoffice.Office.Common/Security/Scripting">'
        '<prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>'
        '<prop oor:name="DisableActiveContent" oor:op="fuse"><value>true</value></prop>'
        '<prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>'
        '<item oor:path="/org.openoffice.Office.Writer/Content/Update">'
        '<prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>'
        '<item oor:path="/org.openoffice.Office.Calc/Content/Update">'
        '<prop oor:name="Link" oor:op="fuse"><value>1</value></prop></item>'
        '</oor:items>', encoding="utf-8"
    )
    kind = request["Format"]
    if kind not in ("docx", "ods", "pdf", "txt", "html", "epub", "csv", "tsv"):
        raise RuntimeError("Unsupported trusted export format")
    filter_options = b""
    if kind in ("csv", "tsv"):
        # Token 12=-1 exports all sheets; token 10=false exports values;
        # token 13=false prevents formula evaluation on delimited imports.
        delimiter = "44" if kind == "csv" else "9"
        filter_options = (delimiter + ",34,76,1,,0,false,true,true,false,false,-1,false,false,false").encode("ascii")
    program = pathlib.Path(sys.executable).resolve().parent
    dll_search = os.add_dll_directory(str(program))
    library = ctypes.CDLL(str(program / "mergedlo.dll"))
    hook = library.libreofficekit_hook_2
    hook.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    hook.restype = ctypes.c_void_p
    print("Initializing isolated LibreOfficeKit", flush=True)
    office = hook(str(program).encode("utf-8"), profile.as_uri().encode("utf-8"))
    if not office:
        raise RuntimeError("The isolated LibreOfficeKit engine could not initialize")

    def method(instance, index, result, arguments):
        table = ctypes.cast(instance, ctypes.POINTER(ctypes.c_void_p))[0]
        size = ctypes.cast(table, ctypes.POINTER(ctypes.c_size_t))[0]
        offset = ctypes.sizeof(ctypes.c_size_t) + index * ctypes.sizeof(ctypes.c_void_p)
        if size < offset + ctypes.sizeof(ctypes.c_void_p):
            raise RuntimeError("The bundled LibreOfficeKit ABI does not provide the required method")
        address = ctypes.cast(table + offset, ctypes.POINTER(ctypes.c_void_p))[0]
        return ctypes.CFUNCTYPE(result, *arguments)(address)

    get_error = method(office, 2, ctypes.c_void_p, [ctypes.c_void_p])
    free_error = method(office, 4, None, [ctypes.c_void_p])

    def error():
        value = get_error(office)
        if not value:
            return "Unknown conversion error"
        try:
            return ctypes.string_at(value).decode("utf-8", errors="replace")[:8192]
        finally:
            free_error(value)

    if source.suffix.lower() in (".xlsx", ".xls", ".ods", ".csv", ".tsv", ".pptx", ".ppt", ".odp"):
        # Windows LOK's visible Calc/Impress load can deadlock before a document is
        # returned. Use its already-initialized in-process UNO context with
        # Hidden=True. This has no resolver, listener or named-pipe transport.
        import uno
        import unohelper
        from com.sun.star.task import XInteractionHandler2

        class RejectInteraction(unohelper.Base, XInteractionHandler2):
            def handle(self, request):
                raise RuntimeError("The offline converter rejected an interactive document request")

            def handleInteractionRequest(self, request):
                return False

        def prop(name, value):
            result = uno.createUnoStruct("com.sun.star.beans.PropertyValue")
            result.Name, result.Value = name, value
            return result

        spreadsheet = source.suffix.lower() in (".xlsx", ".xls", ".ods", ".csv", ".tsv")
        context = uno.getComponentContext()

        def export_hidden():
            desktop = context.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", context)
            options = [prop("Hidden", True), prop("ReadOnly", True), prop("Silent", True),
                       prop("AsTemplate", False), prop("MacroExecutionMode", uno.Any("short", 0)),
                       prop("UpdateDocMode", uno.Any("short", 0)), prop("InteractionHandler", RejectInteraction())]
            if source.suffix.lower() in (".csv", ".tsv"):
                import csv
                delimiter = "," if source.suffix.lower() == ".csv" else "\t"
                with source.open("r", encoding="utf-8-sig", newline="") as text:
                    first = text.readline(32 * 1024 * 1024 + 1)
                if len(first) > 32 * 1024 * 1024:
                    raise RuntimeError("Delimited source exceeds the 32-MiB row limit")
                columns = len(next(csv.reader([first], delimiter=delimiter)))
                if columns > 16384:
                    raise RuntimeError("Delimited source exceeds the 16,384-column limit")
                # CSV has no typed cells. Import fields as literal text so leading
                # zero identifiers and formula-looking content cannot be altered.
                types = "/".join(str(index) + "/2" for index in range(1, columns + 1))
                options += [prop("FilterName", "Text - txt - csv (StarCalc)"),
                            prop("FilterOptions", str(ord(delimiter)) + ",34,76,1," + types + ",0,false,true,false,false,false,0,false,false,false")]
            print("Opening the verified Office document without a visible view", flush=True)
            document = desktop.loadComponentFromURL(source.as_uri(), "_blank", 0, tuple(options))
            if document is None:
                raise RuntimeError("The isolated Office engine could not load this document")
            filters = ({"pdf": "calc_pdf_Export", "ods": "calc8", "csv": "Text - txt - csv (StarCalc)",
                        "tsv": "Text - txt - csv (StarCalc)"} if spreadsheet else {"pdf": "impress_pdf_Export"})
            if kind not in filters:
                raise RuntimeError("Unsupported Office export")
            print("Exporting the converted Office document", flush=True)
            export = [prop("FilterName", filters[kind]), prop("Overwrite", False)]
            if filter_options:
                export.append(prop("FilterOptions", filter_options.decode("ascii")))
            document.storeToURL((output / ("converted." + kind)).as_uri(), tuple(export))
            document.close(True)
            print("Office copy exported", flush=True)
            # The host awaits this whole one-document job's retirement. Avoid
            # process-lifetime UNO proxy finalizers after its synchronous save.
            os._exit(0)

        if spreadsheet:
            export_hidden()
        else:
            # Impress must run on its own initialized Office main loop. A UNO
            # AsyncCallback enters that thread directly, avoiding the Windows
            # cross-thread SolarMutex/clipboard deadlock. No polling or delay.
            from com.sun.star.awt import XCallback
            import threading

            class ExportOnOfficeThread(unohelper.Base, XCallback):
                def notify(self, data):
                    try:
                        export_hidden()
                    except Exception:
                        traceback.print_exc()
                        sys.stdout.flush()
                        sys.stderr.flush()
                        os._exit(1)

            service = context.ServiceManager.createInstanceWithContext("com.sun.star.awt.AsyncCallback", context)
            callback = ExportOnOfficeThread()
            service.addCallback(callback, None)
            print("Awaiting the Office main-loop export", flush=True)
            threading.Event().wait()

    load = method(office, 3, ctypes.c_void_p, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p])
    print("Opening the verified document", flush=True)
    document = load(office, source.as_uri().encode("utf-8"),
                    b"Language=en-US,Batch=true,MacroSecurityLevel=3,EnableMacrosExecution=false")
    if not document:
        raise RuntimeError("The isolated Office engine could not load this document: " + error())
    save = method(document, 1, ctypes.c_int,
                  [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p])
    print("Exporting the converted copy", flush=True)
    # LibreOfficeKit exposes CSV as the Calc filter ID; TSV is that same
    # filter with delimiter 9 and a truthful .tsv filename.
    format_id = "csv" if kind == "tsv" else kind
    if not save(document, (output / ("converted." + kind)).as_uri().encode("utf-8"),
                format_id.encode("ascii"), filter_options):
        raise RuntimeError("The isolated Office export failed: " + error())
    files = sorted(output.glob("*." + kind))
    if not files:
        raise RuntimeError("The isolated LibreOfficeKit export produced no output")
    if len(files) == 1:
        final = output / ("converted." + kind)
        if files[0] != final:
            files[0].rename(final)
        files = [final]
    (request_path.parent / "result.json").write_text(
        json.dumps({"Files": [file.name for file in files]}), encoding="utf-8")
    method(document, 0, None, [ctypes.c_void_p])(document)
    method(office, 0, None, [ctypes.c_void_p])(office)
    dll_search.close()


try:
    main()
except Exception:
    traceback.print_exc()
    sys.stdout.flush()
    sys.stderr.flush()
    # An error must not wait for LibreOffice's process-lifetime threads during
    # Python finalization. The host retires this entire isolated Job Object.
    os._exit(1)

