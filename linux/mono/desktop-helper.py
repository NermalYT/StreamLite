#!/usr/bin/env python3
# StreamLite's desktop helper (Linux): what the desktop offers through GTK and D-Bus, which Mono can't reach.
#   --tray        StreamLite's tray icon and menu as an AppIndicator (StatusNotifierItem): GNOME (with the AppIndicator
#                 extension, standard on Ubuntu), KDE Plasma on Wayland, and anywhere old-style tray icons don't show.
#   --shortcuts   hotkeys on Wayland through the desktop's global-shortcuts portal (GNOME 48+, KDE Plasma 5.27+).
# Talks with StreamLite in lines: in   {"menu": {"title": ..., "items": [...]}} | {"shortcuts": [{"id", "description", "trigger"}]}
#                                 out  "ready tray" | "ready shortcuts" | "no tray: why" | "no shortcuts: why"
#                                      "click 2/1" (menu item path) | "down h0" | "up h0" (shortcut id)
import json, sys, threading

def say(line):
    try:
        sys.stdout.write(line + "\n"); sys.stdout.flush()
    except Exception:
        pass

want_tray = "--tray" in sys.argv
want_keys = "--shortcuts" in sys.argv
icon = next((a for a in sys.argv[1:] if not a.startswith("--")), "audio-input-microphone")

try:
    import gi
    gi.require_version("Gtk", "3.0")
    from gi.repository import Gtk, GLib, Gio
except Exception as e:
    say("no tray: " + str(e)); say("no shortcuts: " + str(e)); sys.exit(0)

# ---- tray (AppIndicator)
indicator = None
if want_tray:
    AI = None
    for name, ver in (("AyatanaAppIndicator3", "0.1"), ("AppIndicator3", "0.1")):
        try:
            gi.require_version(name, ver)
            AI = __import__("gi.repository." + name, fromlist=[name])
            break
        except Exception:
            continue
    if AI is None:
        say("no tray: AppIndicator isn't installed")
    else:
        indicator = AI.Indicator.new("streamlite", icon, AI.IndicatorCategory.APPLICATION_STATUS)
        indicator.set_status(AI.IndicatorStatus.ACTIVE)
        indicator.set_title("StreamLite")
        say("ready tray")

def build(items, prefix):
    menu = Gtk.Menu()
    for i, it in enumerate(items):
        path = prefix + [i]
        if it.get("sep"):
            menu.append(Gtk.SeparatorMenuItem()); continue
        if it.get("check"):
            mi = Gtk.CheckMenuItem(label=it.get("label", "")); mi.set_active(bool(it.get("checked")))
        else:
            mi = Gtk.MenuItem(label=it.get("label", ""))
        mi.set_sensitive(it.get("enabled", True))
        if it.get("items"):
            mi.set_submenu(build(it["items"], path))
        else:
            mi.connect("activate", lambda w, p=path: say("click " + "/".join(str(x) for x in p)))
        menu.append(mi)
    menu.show_all()
    return menu

# ---- global shortcuts (portal)
bus = None
session = None
token = [0]
PORTAL, PATH = "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop"
GS = "org.freedesktop.portal.GlobalShortcuts"

def next_token():
    token[0] += 1
    return "streamlite%d" % token[0]

def request(method, params, on_response):
    """A portal call: its answer comes as the Response signal of a request object named after our token."""
    sender = bus.get_unique_name()[1:].replace(".", "_")
    t = next_token()
    path = "/org/freedesktop/portal/desktop/request/%s/%s" % (sender, t)
    sub = [0]
    def handler(conn, sender_name, obj, iface, signal, args):
        bus.signal_unsubscribe(sub[0])
        code, results = args.unpack()
        on_response(code, results)
    sub[0] = bus.signal_subscribe(PORTAL, "org.freedesktop.portal.Request", "Response", path, None, Gio.DBusSignalFlags.NO_MATCH_RULE, handler)
    params(t)
    return path

def bind(shortcuts):
    def go(t):
        items = [(s["id"], {"description": GLib.Variant("s", s.get("description", s["id"])), "preferred_trigger": GLib.Variant("s", s.get("trigger", ""))}) for s in shortcuts]
        bus.call_sync(PORTAL, PATH, GS, "BindShortcuts",
                      GLib.Variant("(oa(sa{sv})sa{sv})", (session, items, "", {"handle_token": GLib.Variant("s", t)})),
                      None, Gio.DBusCallFlags.NONE, -1, None)
    def done(code, results):
        say("ready shortcuts" if code == 0 else "no shortcuts: the desktop didn't bind them (%d)" % code)
    try:
        request("BindShortcuts", go, done)
    except Exception as e:
        say("no shortcuts: " + str(e))

def start_session(shortcuts):
    global session
    if session:
        try: bus.call_sync(PORTAL, session, "org.freedesktop.portal.Session", "Close", None, None, Gio.DBusCallFlags.NONE, -1, None)
        except Exception: pass
        session = None
    if not shortcuts:
        return
    def go(t):
        bus.call_sync(PORTAL, PATH, GS, "CreateSession",
                      GLib.Variant("(a{sv})", ({"handle_token": GLib.Variant("s", t), "session_handle_token": GLib.Variant("s", next_token())},)),
                      None, Gio.DBusCallFlags.NONE, -1, None)
    def done(code, results):
        global session
        if code != 0 or "session_handle" not in results:
            say("no shortcuts: the desktop refused a session (%d)" % code); return
        session = results["session_handle"]
        bind(shortcuts)
    try:
        request("CreateSession", go, done)
    except Exception as e:
        say("no shortcuts: " + str(e))

if want_keys:
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        version = bus.call_sync(PORTAL, PATH, "org.freedesktop.DBus.Properties", "Get", GLib.Variant("(ss)", (GS, "version")),
                                GLib.VariantType("(v)"), Gio.DBusCallFlags.NONE, 2000, None)
        def on_signal(conn, sender_name, obj, iface, signal, args):
            a = args.unpack()
            if session and a[0] == session:
                say(("down " if signal == "Activated" else "up ") + a[1])
        bus.signal_subscribe(PORTAL, GS, "Activated", PATH, None, Gio.DBusSignalFlags.NONE, on_signal)
        bus.signal_subscribe(PORTAL, GS, "Deactivated", PATH, None, Gio.DBusSignalFlags.NONE, on_signal)
    except Exception as e:
        bus = None
        say("no shortcuts: the desktop has no global-shortcuts portal")

def apply(line):
    try:
        data = json.loads(line)
    except Exception:
        return False
    if "menu" in data and indicator is not None:
        m = data["menu"]
        indicator.set_menu(build(m.get("items", []), []))
        indicator.set_title(m.get("title", "StreamLite"))
    if "shortcuts" in data and bus is not None:
        start_session(data["shortcuts"])
    return False

def reader():
    for line in sys.stdin:
        line = line.strip()
        if line:
            GLib.idle_add(apply, line)
    GLib.idle_add(Gtk.main_quit)   # StreamLite closed: so does the helper

if indicator is not None:
    indicator.set_menu(build([{"label": "StreamLite", "enabled": False}], []))
threading.Thread(target=reader, daemon=True).start()
Gtk.main()
