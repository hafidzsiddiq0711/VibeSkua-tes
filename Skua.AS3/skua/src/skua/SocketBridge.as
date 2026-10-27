package skua {
import flash.display.Sprite;
import flash.events.Event;
import flash.events.IOErrorEvent;
import flash.events.MouseEvent;
import flash.events.ProgressEvent;
import flash.events.SecurityErrorEvent;
import flash.events.TimerEvent;
import flash.net.SharedObject;
import flash.net.Socket;
import flash.text.TextField;
import flash.text.TextFieldType;
import flash.text.TextFormat;
import flash.utils.ByteArray;
import flash.utils.Timer;

/**
 * Transport used when ExternalInterface is not available, e.g. when skua.swf is opened in the
 * Artix Games Launcher (Ruffle) on Android. skua.swf connects to Skua (RemoteFlashUtil in
 * listen mode) over TCP and speaks JSON, one object per line:
 *
 *   Skua -> swf : {"id":7,"fn":"getGameObject","args":["world.myAvatar"]}
 *   swf -> Skua : {"id":7,"r":"..."}
 *   swf -> Skua : {"ev":"pext","args":[["{...}"]]}
 */
public class SocketBridge {
    public static const DEFAULT_PORT:int = 5150;
    private static const RETRY_MS:int = 3000;

    private var root:Sprite;
    private var callbacks:Object = {};
    private var sock:Socket;
    private var host:String;
    private var port:int = DEFAULT_PORT;
    private var pending:ByteArray = new ByteArray();
    private var connected:Boolean = false;
    private var sticky:Object = {};
    private var retryTimer:Timer;
    /** Addresses tried in turn until one answers (e.g. Skua Mobile on this phone, then the PC). */
    private var candidates:Array = [];
    private var candidateIndex:int = 0;

    private var ui:Sprite;
    private var input:TextField;
    private var status:TextField;

    public function SocketBridge(root:Sprite) {
        this.root = root;
    }

    public function addCallback(name:String, func:Function):void {
        callbacks[name] = func;
    }

    /** ActionScript -> Skua event. Same shape as ExternalInterface.call(name, rest). */
    public function call(name:String, rest:Array):void {
        var line:String = JSON.stringify({ev: name, args: [rest]});
        // Replayed on every (re)connect so Skua can start after the movie (and restart).
        if (name == "requestLoadGame")
            sticky[name] = line;
        if (connected)
            writeLine(line);
    }

    public function start():void {
        var saved:String = "";
        try {
            var so:SharedObject = SharedObject.getLocal("skuaBridge");
            if (so.data.host)
                saved = String(so.data.host);
        } catch (e:Error) {
        }
        var param:String = "";
        try {
            var p:* = root.loaderInfo.parameters["skuaHost"];
            if (p != null)
                param = String(p);
        } catch (e3:Error) {
        }

        // skuaHost given (Skua Mobile): only that. Otherwise try Skua on this phone first,
        // then the last PC address typed here, in turn, so nothing has to be typed.
        if (param.length > 0)
            candidates = [param];
        else if (saved.length > 0 && saved != LOCAL)
            candidates = [LOCAL, saved];
        else
            candidates = [LOCAL];
        candidateIndex = 0;

        buildUi(param.length > 0 ? param : (saved.length > 0 ? saved : LOCAL));
        connect(candidates[0], false);
    }

    private static const LOCAL:String = "127.0.0.1";

    // ---------------------------------------------------------------- connection

    private function connect(address:String, remember:Boolean = true):void {
        address = address.replace(/^\s+|\s+$/g, "");
        if (address.length == 0) {
            setStatus("Ketik IP PC (lihat jendela Run-VibeSkua-Remote).");
            return;
        }
        var colon:int = address.lastIndexOf(":");
        if (colon > 0) {
            host = address.substring(0, colon);
            port = int(address.substring(colon + 1)) || DEFAULT_PORT;
        } else {
            host = address;
            port = DEFAULT_PORT;
        }
        if (remember && address != LOCAL) {
            try {
                var so:SharedObject = SharedObject.getLocal("skuaBridge");
                so.data.host = address;
                so.flush();
            } catch (e:Error) {
            }
        }
        openSocket();
    }

    private function openSocket():void {
        stopRetry();
        closeSocket();
        setStatus("Menyambung ke " + host + ":" + port + " ...");
        sock = new Socket();
        sock.timeout = 10000;
        sock.addEventListener(Event.CONNECT, onConnect);
        sock.addEventListener(Event.CLOSE, onClose);
        sock.addEventListener(IOErrorEvent.IO_ERROR, onError);
        sock.addEventListener(SecurityErrorEvent.SECURITY_ERROR, onError);
        sock.addEventListener(ProgressEvent.SOCKET_DATA, onData);
        try {
            sock.connect(host, port);
        } catch (e:Error) {
            onError(null);
        }
    }

    private function closeSocket():void {
        if (sock == null)
            return;
        sock.removeEventListener(Event.CONNECT, onConnect);
        sock.removeEventListener(Event.CLOSE, onClose);
        sock.removeEventListener(IOErrorEvent.IO_ERROR, onError);
        sock.removeEventListener(SecurityErrorEvent.SECURITY_ERROR, onError);
        sock.removeEventListener(ProgressEvent.SOCKET_DATA, onData);
        try {
            if (sock.connected)
                sock.close();
        } catch (e:Error) {
        }
        sock = null;
        connected = false;
        pending = new ByteArray();
    }

    private function onConnect(e:Event):void {
        connected = true;
        setStatus("Tersambung ke Skua di " + host + ":" + port);
        writeLine('{"ev":"__hello","args":[1]}');
        for (var k:String in sticky)
            writeLine(sticky[k]);
    }

    private function onClose(e:Event):void {
        closeSocket();
        setStatus("Skua terputus. Coba lagi tiap 3 detik ...");
        scheduleRetry();
    }

    private function onError(e:Event):void {
        closeSocket();
        setStatus("Gagal menyambung ke " + host + ":" + port + ". Coba lagi tiap 3 detik ...");
        scheduleRetry();
    }

    private function scheduleRetry():void {
        if (host == null || retryTimer != null)
            return;
        retryTimer = new Timer(RETRY_MS, 1);
        retryTimer.addEventListener(TimerEvent.TIMER_COMPLETE, function(e:TimerEvent):void {
            retryTimer = null;
            if (candidates.length > 1) {
                candidateIndex = (candidateIndex + 1) % candidates.length;
                connect(candidates[candidateIndex], false);
                return;
            }
            openSocket();
        });
        retryTimer.start();
    }

    private function stopRetry():void {
        if (retryTimer != null) {
            retryTimer.stop();
            retryTimer = null;
        }
    }

    // ---------------------------------------------------------------- protocol

    private function onData(e:ProgressEvent):void {
        if (sock == null)
            return;
        sock.readBytes(pending, pending.length, sock.bytesAvailable);
        var start:uint = 0;
        var len:uint = pending.length;
        for (var i:uint = 0; i < len; i++) {
            if (pending[i] == 10) {
                pending.position = start;
                var line:String = pending.readUTFBytes(i - start);
                start = i + 1;
                if (line.length > 0)
                    handleLine(line);
            }
        }
        if (start > 0) {
            var rest:ByteArray = new ByteArray();
            if (start < pending.length) {
                pending.position = start;
                pending.readBytes(rest, 0, pending.length - start);
            }
            pending = rest;
        }
    }

    private function handleLine(line:String):void {
        var msg:Object;
        try {
            msg = JSON.parse(line);
        } catch (e:Error) {
            return;
        }
        if (msg == null || msg.fn == null)
            return;
        var id:int = int(msg.id);
        var name:String = String(msg.fn);
        var result:* = null;
        if (name == "__ping") {
            result = "pong";
        } else {
            var func:Function = callbacks[name] as Function;
            if (func != null) {
                try {
                    result = func.apply(null, msg.args is Array ? msg.args : []);
                } catch (err:Error) {
                    result = null;
                }
            }
        }
        if (result === undefined)
            result = null;
        writeLine(JSON.stringify({id: id, r: result}));
    }

    private function writeLine(line:String):void {
        if (sock == null || !sock.connected)
            return;
        try {
            sock.writeUTFBytes(line);
            sock.writeByte(10);
            sock.flush();
        } catch (e:Error) {
        }
    }

    // ---------------------------------------------------------------- UI (until the game loads)

    private function buildUi(saved:String):void {
        ui = new Sprite();
        ui.graphics.beginFill(0x000000, 1);
        ui.graphics.drawRect(0, 0, 958, 550);
        ui.graphics.endFill();

        var title:TextField = label("VibeSkua Remote", 34, 0xFDAF2D);
        title.x = 60;
        title.y = 50;
        ui.addChild(title);

        var hint:TextField = label("Skua Mobile di HP ini: 127.0.0.1. VibeSkua di PC: ketik IP PC (lihat jendela Run-VibeSkua-Remote).", 20, 0xFFFFFF);
        hint.x = 60;
        hint.y = 130;
        hint.width = 840;
        ui.addChild(hint);

        input = new TextField();
        input.type = TextFieldType.INPUT;
        input.defaultTextFormat = new TextFormat("_sans", 34, 0x000000);
        input.background = true;
        input.backgroundColor = 0xFFFFFF;
        input.border = true;
        input.borderColor = 0xFDAF2D;
        input.x = 60;
        input.y = 180;
        input.width = 560;
        input.height = 56;
        input.text = saved;
        ui.addChild(input);

        var button:Sprite = new Sprite();
        button.graphics.beginFill(0xFDAF2D, 1);
        button.graphics.drawRoundRect(0, 0, 240, 56, 12, 12);
        button.graphics.endFill();
        var buttonText:TextField = label("Sambungkan", 28, 0x000000);
        buttonText.x = 30;
        buttonText.y = 8;
        buttonText.mouseEnabled = false;
        button.addChild(buttonText);
        button.buttonMode = true;
        button.x = 640;
        button.y = 180;
        button.addEventListener(MouseEvent.CLICK, function(e:MouseEvent):void {
            candidates = [input.text];
            candidateIndex = 0;
            connect(input.text);
        });
        ui.addChild(button);

        status = label("", 22, 0x8CD5FF);
        status.x = 60;
        status.y = 270;
        status.width = 840;
        ui.addChild(status);

        root.addChild(ui);
        setStatus("");
    }

    private function label(text:String, size:int, color:uint):TextField {
        var t:TextField = new TextField();
        t.defaultTextFormat = new TextFormat("_sans", size, color, true);
        t.selectable = false;
        t.multiline = true;
        t.wordWrap = true;
        t.width = 600;
        t.height = size * 3;
        t.text = text;
        return t;
    }

    private function setStatus(text:String):void {
        if (status != null)
            status.text = text;
    }
}
}
