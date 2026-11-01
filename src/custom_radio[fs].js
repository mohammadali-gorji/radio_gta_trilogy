// In-game half of Custom Radio. Only uses the official CLEO Redux IniFile plugin: no raw memory, no DLL calls.
// It reports "player is in a car" + a heartbeat to radio_bridge.ini; the hidden CustomRadio.exe helper does the audio.
if (HOST !== "sa_unreal") exit("Custom radio: wrong game");

// This script lives in <Win64>\CLEO; the helper's folder is <Win64>\CustomRadio.
const BS = String.fromCharCode(92); // backslash, spelled out so no escaping can go wrong
const dirParts = __dirname.split("/").join(BS).split(BS).filter(function (p) { return p.length > 0; });
const WIN64_DIR = dirParts.slice(0, -1).join(BS);
const BRIDGE = WIN64_DIR + BS + "CustomRadio" + BS + "radio_bridge.ini";

const player = new Player(0);
let beat = 0, lastInCar = -1, lastNowId = IniFile.ReadInt(BRIDGE, "Now", "Id") || 0;
log("Custom radio bridge: " + BRIDGE);

while (true) {
  wait(250);
  let inCar = 0;
  try { inCar = player.getChar().isInAnyCar() ? 1 : 0; } catch (_) { inCar = 0; }
  if (inCar !== lastInCar) { IniFile.WriteInt(inCar, BRIDGE, "Game", "InCar"); lastInCar = inCar; }
  beat = (beat + 1) % 100000;
  IniFile.WriteInt(beat, BRIDGE, "Game", "Beat");

  const nowId = IniFile.ReadInt(BRIDGE, "Now", "Id") || 0;
  if (nowId !== lastNowId) {
    lastNowId = nowId;
    const title = IniFile.ReadString(BRIDGE, "Now", "Title");
    if (inCar && title) showTextBox("Radio: " + title);
  }
}
