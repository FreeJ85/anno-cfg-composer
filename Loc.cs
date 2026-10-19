using System.ComponentModel;
using System.Globalization;

namespace AnnoCfgComposer;

public enum Lang { En, De, Ko }

/// <summary>
/// UI strings — English, German, Korean. Each key maps to [en, de, ko].
/// The first run follows the Windows display language (ko → Korean, de → German, otherwise English); the chosen language is saved in settings.json.
/// </summary>
public static class Loc
{
    static Lang current = Detect();
    public static Lang Current
    {
        get => current;
        set
        {
            current = value;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(CopyPathText)));
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(RemoveModText)));
        }
    }

    /// <summary>For static bindings in XAML — WPF re-reads it when the language changes.</summary>
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;
    public static string CopyPathText => S("ctx.copy");
    public static string RemoveModText => S("ctx.removeMod");

    public static Lang Detect() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "ko" => Lang.Ko,
        "de" => Lang.De,
        _ => Lang.En,
    };

    public static string Code(Lang l) => l switch { Lang.De => "de", Lang.Ko => "ko", _ => "en" };
    public static Lang FromCode(string? code) => code switch { "de" => Lang.De, "ko" => Lang.Ko, "en" => Lang.En, _ => Detect() };

    public static string S(string key) => T.TryGetValue(key, out var v) ? v[(int)Current] : key;
    // Number formatting follows the language too (German 15.240 · 0,4 s)
    static readonly CultureInfo[] Cultures = [new("en-US"), new("de-DE"), new("ko-KR")];

    public static string F(string key, params object?[] args) => string.Format(Cultures[(int)Current], S(key), args);

    static readonly Dictionary<string, string[]> T = new()
    {
        // Toolbar
        ["btn.game"] = ["Game folder…", "Spielordner…", "게임 폴더…"],
        ["tip.game"] = ["Anno 117 install folder (the folder that contains maindata)", "Anno-117-Installationsordner (der Ordner mit maindata)", "Anno 117 설치 폴더 (maindata 가 들어 있는 폴더)"],
        ["btn.open"] = ["Open cfg from disk…", "cfg von Festplatte öffnen…", "디스크의 cfg 열기…"],
        ["tip.open"] = ["A cfg in a mod folder, for example. Files are looked up in that cfg's data folder first, then in the game.",
                        "Z. B. eine cfg aus einem Mod-Ordner. Dateien werden zuerst im data-Ordner dieser cfg gesucht, dann im Spiel.",
                        "모드 폴더 등에 있는 cfg. 그 cfg 의 data 폴더를 먼저 보고, 없는 파일은 게임에서 찾습니다."],
        ["btn.extract"] = ["Extract with dependencies…", "Mit Abhängigkeiten extrahieren…", "딸린 파일과 함께 추출…"],
        ["tip.extract"] = ["Extracts prop cfgs, rdm models, dds textures and ifo/fc files, keeping the data/... structure",
                           "Extrahiert Prop-cfgs, rdm-Modelle, dds-Texturen und ifo/fc-Dateien mit der data/...-Struktur",
                           "소품 cfg · 모델 rdm · 텍스처 dds · ifo/fc 를 data/... 구조 그대로 뽑습니다"],
        ["chk.mirror"] = ["Mirror", "Spiegeln", "좌우 반전"],
        ["tip.mirror"] = ["Converts Anno's left-handed coordinates for the screen. Turn it off if buildings look mirrored compared to the game.",
                          "Wandelt Annos linkshändige Koordinaten für die Anzeige um. Ausschalten, wenn Gebäude im Vergleich zum Spiel gespiegelt aussehen.",
                          "Anno 좌표계(왼손)를 화면에 맞추기. 게임과 좌우가 반대로 보이면 끄세요."],
        ["chk.textures"] = ["Textures", "Texturen", "텍스처"],
        ["chk.grid"] = ["Grid", "Raster", "격자"],
        ["btn.zoom"] = ["Fit view", "Alles zeigen", "전체 보기"],
        ["tip.lang"] = ["Language", "Sprache", "언어"],
        ["tip.search"] = ["Only paths that contain every space-separated word (e.g. kontor roman)",
                          "Nur Pfade, die alle durch Leerzeichen getrennten Wörter enthalten (z. B. kontor roman)",
                          "공백으로 나눈 단어가 모두 들어간 경로만 (예: kontor roman)"],
        ["hint.pick"] = ["Pick a cfg on the left", "Links eine cfg auswählen", "왼쪽에서 cfg 를 고르세요"],

        // Status bar
        ["st.ready"] = ["Ready", "Bereit", "준비"],
        ["st.noGame"] = ["Anno 117 install folder not found. Choose it with [Game folder…].",
                         "Anno-117-Installationsordner nicht gefunden. Bitte über [Spielordner…] wählen.",
                         "Anno 117 설치 폴더를 못 찾았습니다 — [게임 폴더…] 로 골라 주세요."],
        ["st.reading"] = ["Reading archives ({0}/{1}) {2}", "Lese Archive ({0}/{1}) {2}", "아카이브 읽는 중 ({0}/{1}) {2}"],
        ["st.archiveFail"] = ["Could not read {0}: {1}", "{0} konnte nicht gelesen werden: {1}", "{0} 읽기 실패: {1}"],
        ["st.loaded"] = ["{0} · {1} archives · {2:N0} files · {3:N0} cfg ({4:0.0} s)",
                         "{0} · {1} Archive · {2:N0} Dateien · {3:N0} cfg ({4:0.0} s)",
                         "{0} · 아카이브 {1}개 · 파일 {2:N0}개 · cfg {3:N0}개 ({4:0.0}초)"],
        ["st.gameFail"] = ["Could not read the game archives: {0}", "Spielarchive konnten nicht gelesen werden: {0}", "게임 아카이브를 읽지 못했습니다: {0}"],
        ["st.loading"] = ["Loading… {0}", "Lade… {0}", "불러오는 중… {0}"],
        ["st.shown"] = ["{0} · {1} models · {2:N0} triangles · {3} ms", "{0} · {1} Modelle · {2:N0} Dreiecke · {3} ms", "{0} · 모델 {1}개 · 삼각형 {2:N0} · {3} ms"],
        ["st.loadFail"] = ["Loading failed: {0}", "Laden fehlgeschlagen: {0}", "불러오기 실패: {0}"],
        ["st.deps"] = ["Collecting dependencies…", "Suche Abhängigkeiten…", "딸린 파일 찾는 중…"],
        ["st.extracting"] = ["Extracting… {0}", "Extrahiere… {0}", "추출 중… {0}개"],
        ["st.extracted"] = ["{0} files extracted → {1}", "{0} Dateien extrahiert → {1}", "{0}개 추출 → {1}"],
        ["st.extractFail"] = ["Extraction failed: {0}", "Extraktion fehlgeschlagen: {0}", "추출 실패: {0}"],

        ["cnt.all"] = ["{0:N0} cfg", "{0:N0} cfg", "cfg {0:N0}개"],
        ["cnt.filtered"] = ["{0:N0} / {1:N0}", "{0:N0} / {1:N0}", "{0:N0} / {1:N0}개"],

        // Dialogs
        ["dlg.gameTitle"] = ["Anno 117 install folder (contains maindata)", "Anno-117-Installationsordner (enthält maindata)", "Anno 117 설치 폴더 (maindata 가 들어 있는 폴더)"],
        ["dlg.gameCaption"] = ["Game folder", "Spielordner", "게임 폴더"],
        ["dlg.notGame"] = ["This folder has no maindata\\graphics_roman.rda.\nPlease choose the Anno 117 install folder.",
                           "Dieser Ordner enthält kein maindata\\graphics_roman.rda.\nBitte den Anno-117-Installationsordner wählen.",
                           "이 폴더에 maindata\\graphics_roman.rda 가 없습니다.\nAnno 117 설치 폴더를 골라 주세요."],
        ["dlg.openFilter"] = ["Anno graphics cfg (*.cfg)|*.cfg|All files|*.*", "Anno-Grafik-cfg (*.cfg)|*.cfg|Alle Dateien|*.*", "Anno 그래픽 cfg (*.cfg)|*.cfg|모든 파일|*.*"],
        ["dlg.openTitle"] = ["Open cfg", "cfg öffnen", "cfg 열기"],
        ["dlg.extractTitle"] = ["Folder to extract to (a data\\... structure is created inside)", "Zielordner (darin wird eine data\\...-Struktur angelegt)", "추출할 폴더 (안에 data\\... 구조로 만듭니다)"],
        ["dlg.extractDone"] = ["{0} files extracted.\n{1}\n", "{0} Dateien extrahiert.\n{1}\n", "{0}개 파일을 추출했습니다.\n{1}\n"],
        ["dlg.extractMissing"] = ["\n{0} paths not found:\n", "\n{0} Pfade nicht gefunden:\n", "\n못 찾은 경로 {0}개:\n"],
        ["dlg.openFolder"] = ["\nOpen the folder?", "\nOrdner öffnen?", "\n폴더를 열까요?"],
        ["dlg.extractCaption"] = ["Extract", "Extrahieren", "추출"],
        ["dlg.extractFailCaption"] = ["Extraction failed", "Extraktion fehlgeschlagen", "추출 실패"],
        ["dlg.errorCaption"] = ["Anno Cfg Composer error", "Anno Cfg Composer – Fehler", "Anno Cfg Composer 오류"],

        // Right-hand details
        ["info.source"] = ["Source: {0}", "Quelle: {0}", "출처: {0}"],
        ["info.none"] = ["(none)", "(keine)", "(없음)"],
        ["info.counts"] = ["{0} cfg · {1} models · {2:N0} triangles", "{0} cfg · {1} Modelle · {2:N0} Dreiecke", "cfg {0}개 · 모델 {1}개 · 삼각형 {2:N0}"],
        ["info.decals"] = ["{0} decals (not shown yet)", "{0} Decals (noch nicht angezeigt)", "데칼 {0}개 (아직 표시 안 함)"],
        ["info.time"] = ["Loaded in {0:N0} ms", "Geladen in {0:N0} ms", "불러오기 {0:N0} ms"],
        ["info.missing"] = ["Files not found", "Nicht gefundene Dateien", "못 찾은 파일"],
        ["info.missingTex"] = ["Textures not found", "Nicht gefundene Texturen", "못 찾은 텍스처"],
        ["info.broken"] = ["Files that could not be read", "Nicht lesbare Dateien", "읽지 못한 파일"],
        ["info.skipped"] = ["FILE entries not shown (not a cfg)", "Nicht angezeigte FILE-Einträge (keine cfg)", "표시하지 않은 FILE 항목 (cfg 아님)"],
        ["info.parts"] = ["Structure", "Aufbau", "구성"],
        ["kind.model"] = ["model", "Modell", "모델"],
        ["kind.cfg"] = ["cfg", "cfg", "cfg"],

        // Menu / panels (2026-10-19 UI redesign)
        ["menu.file"] = ["_File", "_Datei", "파일(_F)"],
        ["menu.view"] = ["_View", "_Ansicht", "보기(_V)"],
        ["menu.lang"] = ["_Language", "_Sprache", "언어(_L)"],
        ["menu.help"] = ["_Help", "_Hilfe", "도움말(_H)"],
        ["menu.exit"] = ["E_xit", "_Beenden", "끝내기(_X)"],
        ["menu.about"] = ["_About Anno Cfg Composer", "_Über Anno Cfg Composer", "Anno Cfg Composer 정보(_A)"],
        ["menu.axes"] = ["Axes", "Achsen", "좌표축"],
        ["tb.open"] = ["Open", "Öffnen", "열기"],
        ["tb.extract"] = ["Extract", "Extrahieren", "추출"],
        ["panel.files"] = ["GAME FILES", "SPIELDATEIEN", "게임 파일"],
        ["panel.info"] = ["DETAILS", "DETAILS", "정보"],
        ["search.placeholder"] = ["Search cfg…", "cfg suchen…", "cfg 검색…"],
        ["info.empty"] = ["Pick a cfg on the left to see its details here.", "Wähle links eine cfg, um hier Details zu sehen.", "왼쪽에서 cfg 를 고르면 여기에 정보가 나옵니다."],
        ["stat.cfg"] = ["cfg files", "cfg-Dateien", "cfg 파일"],
        ["stat.models"] = ["models", "Modelle", "모델"],
        ["stat.tris"] = ["triangles", "Dreiecke", "삼각형"],
        ["stat.time"] = ["load time", "Ladezeit", "불러온 시간"],
        ["stat.ms"] = ["{0:N0} ms", "{0:N0} ms", "{0:N0} ms"],
        ["tab.parts"] = ["Structure", "Aufbau", "구성"],
        ["tab.issues"] = ["Issues ({0})", "Probleme ({0})", "문제 ({0})"],
        ["issues.none"] = ["No issues.", "Keine Probleme.", "문제 없음."],
        ["warn.summary"] = ["{0} files could not be found or read. See the Issues tab.", "{0} Dateien wurden nicht gefunden oder nicht gelesen. Siehe Reiter Probleme.", "파일 {0}개를 못 찾았거나 읽지 못했습니다. [문제] 탭을 보세요."],
        ["ctx.copy"] = ["Copy path", "Pfad kopieren", "경로 복사"],
        ["hint.mouse"] = ["Left drag: rotate · Right drag: pan · Wheel: zoom", "Links ziehen: drehen · Rechts ziehen: verschieben · Rad: zoomen", "왼쪽 드래그: 회전 · 오른쪽 드래그: 이동 · 휠: 확대"],
        ["st.game"] = ["{0} archives · {1:N0} files · {2:N0} cfg", "{0} Archive · {1:N0} Dateien · {2:N0} cfg", "아카이브 {0}개 · 파일 {1:N0}개 · cfg {2:N0}개"],
        // glTF export
        ["btn.gltf"] = ["Export glTF (.glb)…", "Als glTF exportieren (.glb)…", "glTF 로 내보내기 (.glb)…"],
        ["tb.gltf"] = ["glTF", "glTF", "glTF"],
        ["tip.gltf"] = ["Saves the cfg with all props, models and textures as one .glb file that Blender and other 3D tools open directly (File → Import → glTF 2.0). One-way: it cannot be turned back into a cfg.",
                        "Speichert die cfg mit allen Props, Modellen und Texturen als eine .glb-Datei, die Blender und andere 3D-Programme direkt öffnen (Datei → Importieren → glTF 2.0). Nur in eine Richtung: Sie lässt sich nicht zurück in eine cfg wandeln.",
                        "cfg 를 소품 · 모델 · 텍스처째 .glb 파일 하나로 저장합니다. Blender 등 3D 프로그램이 바로 엽니다 (File → Import → glTF 2.0). 한 방향 — 다시 cfg 로 되돌리지는 못합니다."],
        ["dlg.gltfFilter"] = ["glTF binary (*.glb)|*.glb", "glTF-Binärdatei (*.glb)|*.glb", "glTF 바이너리 (*.glb)|*.glb"],
        ["dlg.gltfTitle"] = ["Export glTF", "glTF exportieren", "glTF 로 내보내기"],
        ["st.gltf"] = ["Exporting glTF…", "Exportiere glTF…", "glTF 내보내는 중…"],
        ["st.gltfDone"] = ["glTF saved: {0} ({1:N1} MB)", "glTF gespeichert: {0} ({1:N1} MB)", "glTF 저장: {0} ({1:N1} MB)"],
        ["st.gltfFail"] = ["glTF export failed: {0}", "glTF-Export fehlgeschlagen: {0}", "glTF 내보내기 실패: {0}"],
        ["dlg.gltfDone"] = ["Saved:\n{0}\n\n{1:N0} nodes · {2:N0} meshes · {3:N0} textures · {4:N1} MB\n\nOpen the folder?",
                            "Gespeichert:\n{0}\n\n{1:N0} Knoten · {2:N0} Meshes · {3:N0} Texturen · {4:N1} MB\n\nOrdner öffnen?",
                            "저장했습니다:\n{0}\n\n노드 {1:N0}개 · 메시 {2:N0}개 · 텍스처 {3:N0}개 · {4:N1} MB\n\n폴더를 열까요?"],
        ["menu.gpu"] = ["Graphics card", "Grafikkarte", "그래픽 카드"],
        ["menu.gpuAuto"] = ["Automatic (card connected to the display)", "Automatisch (Karte am Bildschirm)", "자동 (화면에 연결된 카드)"],
        ["st.gpu"] = ["GPU: {0}", "GPU: {0}", "GPU: {0}"],
        // Mod folders (drag and drop)
        ["menu.addMod"] = ["Add mod folder…", "Mod-Ordner hinzufügen…", "모드 폴더 추가…"],
        ["ctx.removeMod"] = ["Remove from list", "Aus der Liste entfernen", "목록에서 빼기"],
        ["hint.drop"] = ["Drop a mod folder or a .cfg here", "Mod-Ordner oder .cfg hierher ziehen", "모드 폴더나 .cfg 를 여기로 끌어다 놓으세요"],
        ["tree.mod"] = ["[Mod] {0}", "[Mod] {0}", "[모드] {0}"],
        ["st.modAdded"] = ["Mod folder added: {0} ({1:N0} cfg)", "Mod-Ordner hinzugefügt: {0} ({1:N0} cfg)", "모드 폴더 추가: {0} (cfg {1:N0}개)"],
        ["st.modNoCfg"] = ["No .cfg files in {0}", "Keine .cfg-Dateien in {0}", "{0} 에 .cfg 파일이 없습니다"],
        ["dlg.modTitle"] = ["Mod folder (the folder that contains data)", "Mod-Ordner (der Ordner mit data)", "모드 폴더 (data 가 들어 있는 폴더)"],
        ["dlg.errorBody"] = ["An error occurred:\n{0}\n\nDetails were saved to:\n{1}\n\nPlease attach this file when you report the problem.",
                             "Ein Fehler ist aufgetreten:\n{0}\n\nDetails wurden gespeichert in:\n{1}\n\nBitte diese Datei beim Melden des Problems anhängen.",
                             "오류가 났습니다:\n{0}\n\n자세한 내용을 저장했습니다:\n{1}\n\n문제를 알릴 때 이 파일을 함께 보내 주세요."],
        ["menu.logs"] = ["Open error log folder", "Fehlerprotokoll-Ordner öffnen", "오류 기록 폴더 열기"],
        ["hint.loading"] = ["Loading…", "Lade…", "불러오는 중…"],
        ["st.copied"] = ["Copied: {0}", "Kopiert: {0}", "복사함: {0}"],
        ["about.text"] = ["Anno Cfg Composer {0}\n\nBrowses the graphics .cfg files of the installed Anno 117, shows them in 3D and extracts them together with every file they use.\n\nfreej85",
                          "Anno Cfg Composer {0}\n\nDurchsucht die Grafik-.cfg-Dateien des installierten Anno 117, zeigt sie in 3D und extrahiert sie mit allen verwendeten Dateien.\n\nfreej85",
                          "Anno Cfg Composer {0}\n\n설치된 Anno 117 의 그래픽 .cfg 를 둘러보고, 3D 로 보고, 쓰는 파일 전부와 함께 추출합니다.\n\nfreej85"],

        // Read errors
        ["err.texFormat"] = ["Unsupported texture format", "Texturformat wird nicht unterstützt", "텍스처 형식을 못 읽음"],
        ["err.rdmHeader"] = ["Not an RDM file", "Keine RDM-Datei", "RDM 머리글 아님"],
        ["err.range"] = ["Out of range @{0}", "Außerhalb des Bereichs @{0}", "범위 밖 @{0}"],
        ["err.vb"] = ["Vertex buffer out of range", "Vertexpuffer außerhalb des Bereichs", "정점 버퍼 범위"],
        ["err.ib"] = ["Index buffer out of range", "Indexpuffer außerhalb des Bereichs", "인덱스 버퍼 범위"],
        ["err.vf"] = ["Invalid vertex format", "Ungültiges Vertexformat", "정점 형식"],
        ["err.vtype"] = ["Unknown vertex type {0} (semantic {1})", "Unbekannter Vertextyp {0} (Semantik {1})", "모르는 정점 형 {0} (의미 {1})"],
        ["err.stride"] = ["Vertex size mismatch {0} ≠ {1}", "Vertexgröße passt nicht: {0} ≠ {1}", "정점 크기 불일치 {0} ≠ {1}"],
        ["err.noPos"] = ["No position element", "Kein Positionselement", "위치 원소 없음"],
        ["err.index"] = ["Index exceeds the vertex count", "Index größer als die Vertexanzahl", "인덱스가 정점 수를 넘음"],
    };
}
