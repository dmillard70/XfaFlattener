# CLAUDE.md — XFA PDF Flattener (Projektplan)

## Projektübersicht

**Name:** `XfaFlattener`
**Typ:** Windows-Kommandozeilen-Applikation (.NET 8 LTS, Self-Contained)
**Ziel:** XFA-basierte PDF-Dokumente (statisch und dynamisch) in normale, geflattete PDFs konvertieren.Nicht-XFA-PDFs sollen übersprungen werden.

**Zielplattformen:**
- Windows 11 (x64)
- Windows Server 2019, 2022, 2025 (x64)

---

## Architektur-Entscheidung: Rendering-Strategie

### Problemanalyse

XFA (XML Forms Architecture) ist eine proprietäre Adobe-Technologie, die in PDF 2.0 (ISO 32000-2) offiziell **deprecated** wurde. Kein Open-Source-Tool bietet 100% Kompatibilität mit allen dynamischen XFA-Features. Die Herausforderungen:

| Feature | Beschreibung | Schwierigkeitsgrad |
|---|---|---|
| Statisches XFA | Festes Layout, Felder mit Daten | Mittel |
| Dynamisches XFA | Layout ändert sich basierend auf Daten | Hoch |
| Repeating Subforms | Tabellenzeilen, die sich dynamisch vermehren | Hoch |
| FormCalc Scripts | Adobe-proprietäre Skriptsprache | Sehr hoch |
| JavaScript in XFA | Eigene JS-API, nicht AcroForm-kompatibel | Hoch |
| Conditional Visibility | Felder/Seiten die basierend auf Logik ein/ausgeblendet werden | Hoch |
| Dynamic Page Reflow | Seitenanzahl ändert sich je nach Datenmenge | Sehr hoch |

### Gewählte Strategie: Multi-Engine-Pipeline mit PDFium (V8+XFA) als Primär-Engine

```
┌─────────────────────────────────────────────────────────────┐
│                    INPUT: XFA-PDF                           │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 1: ANALYSE & XFA-ERKENNUNG                           │
│  - PDF öffnen mit PDFSharp 6                                │
│  - XFA-Streams identifizieren (/Root/AcroForm/XFA)          │
│  - XFA-Typ bestimmen (statisch vs. dynamisch)               │
│  - Optional: XFA-XML exportieren (--export-xfa Flag)        │
│  - Wenn kein XFA → Datei unverändert kopieren/überspringen  │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 2: RENDERING (Primär: PDFium mit V8+XFA)             │
│  - PDFium mit XFA-Support laden (FPDF_LoadXFA)              │
│  - Form Fill Environment initialisieren                     │
│  - JavaScript/FormCalc Execution triggern                   │
│  - Jede Seite in Standard PDF wandeln; falls zu aufwändig   │
│    - Seite bei konfigurierbarer DPI rendern                 │
│    - Ergebnis: Bitmap pro Seite                             │
│    - OCR Layer hinterlegen                                  │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 3: VALIDIERUNG                                       │
│  - Prüfe: Wurden Seiten gerendert? (keine leeren Bitmaps)   │
│  - Prüfe: Seitenanzahl plausibel?                           │
│  - Bei Fehler → Fallback auf Playwright/Chromium             │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 2b: FALLBACK (Playwright/Chromium Print-to-PDF)      │
│  - Nur bei PDFium-Fehler oder --force-playwright Flag       │
│  - PDF in Chromium laden (file:// oder data: URI)           │
│  - page.pdf() für Vektor-Output                             │
│  - Chromium ist gebundelt für Offline-Betrieb               │
└─────────────────┬───────────────────────────────────────────┘
                  │
                  ▼
┌─────────────────────────────────────────────────────────────┐
│  Phase 4: PDF-ASSEMBLY                                      │
│  - PDFSharp 6: Neues PDF erstellen                          │
│  - Gerenderte Seiten / Bitmaps als Seiten einbetten         │
│  - Original-Metadaten übernehmen (Titel, Autor, etc.)       │
│  - Bei Playwright-Output: PDF direkt verwenden              │
│  - Ausgabe speichern                                        │
└─────────────────────────────────────────────────────────────┘
```

### Begründung der Multi-Engine-Strategie

1. **PDFium mit V8+XFA (Primär):** Foxit hat eine umfangreiche XFA-Implementierung zu PDFium beigesteuert. Mit V8-JavaScript-Engine und XFA-Support kann PDFium die meisten dynamischen XFA-Formulare korrekt rendern, inklusive FormCalc und JavaScript-Ausführung. Dies ist der zuverlässigste Open-Source-Ansatz für dynamische XFA.

2. **Playwright/Chromium (Fallback):** Chromium verwendet intern ebenfalls PDFium, bietet aber über `page.pdf()` **Vektor-Output mit selektierbarem Text**. Für einfachere XFA-Formulare kann dies ein besseres Ergebnis liefern. Chrome hat experimentellen XFA-Support, der für statische und einfache dynamische Formulare funktioniert.

3. **Kombination:** PDFium-Rendering für maximale XFA-Kompatibilität, Playwright als Fallback für Fälle wo PDFium versagt, und als Alternative wenn Text-Selektierbarkeit wichtiger ist als XFA-Treue.

---

## XFA-Spezifikation: Technische Details

### XFA-Struktur innerhalb einer PDF

XFA-Daten sind in der PDF als XML-Streams im `/Root/AcroForm/XFA`-Dictionary gespeichert. Der XFA-Stream enthält mehrere **Packets**:

```
XFA Stream
├── preamble        (optional, XML-Deklaration)
├── template        (★ KERN: XFA-Formular-Template, definiert Layout und Felder)
├── datasets        (★ KERN: Die tatsächlichen Formulardaten)
│   ├── data        (Nutzdaten)
│   └── dataDescription (Schema der Daten)
├── config          (Rendering-Konfiguration)
├── connectionSet   (Datenbank-Verbindungen, Web-Services)
├── localeSet       (Lokalisierung: Datums-/Zahlenformate)
├── sourceSet       (Datenquellen)
├── stylesheet      (CSS-ähnliche Styles)
├── xdc             (XFA Data Connection)
├── xfdf            (Form Field Daten im XFDF-Format)
├── xmpmeta         (XMP-Metadaten)
└── postamble       (optional)
```

### XFA-Erkennung (Phase 1 - Implementierungsdetails)

Die XFA-Erkennung muss zuverlässig zwischen diesen Typen unterscheiden:

1. **Kein XFA (reines AcroForm):** Kein `/XFA`-Key im AcroForm-Dictionary → Überspringen
2. **Hybrid-XFA:** Sowohl AcroForm-Felder als auch XFA-Stream vorhanden. Der XFA-Stream hat Vorrang, aber das AcroForm bietet einen Fallback-Rendering-Pfad.
3. **Reines XFA (statisch):** Nur XFA-Stream, Layout ist fixiert (XFA 2.0 und früher)
4. **Reines XFA (dynamisch):** Layout passt sich an Daten an (XFA 2.1+). Erkennbar am `<subform layout="tb">` (top-to-bottom flow) oder `layout="rl-tb"` im Template.

**Erkennungs-Algorithmus:**
```
1. PDF laden mit PDFSharp
2. Navigiere zu /Root → /AcroForm → /XFA
3. Wenn /XFA nicht existiert → KEIN_XFA
4. XFA-Stream extrahieren (kann einzelner Stream oder Array von Name/Stream-Paaren sein)
5. Template-Packet parsen
6. Nach <subform> mit layout="tb"|"rl-tb"|"lr-tb" suchen
7. Wenn gefunden → DYNAMISCH_XFA
8. Sonst → STATISCH_XFA
```

### XFA-Datenextraktion (Optional, --export-xfa)

Vor dem Flattening können die XFA-Daten als Backup exportiert werden:
- Gesamter XFA-Stream als `.xfa.xml`
- Nur Datasets-Packet als `.data.xml`
- Template-Packet als `.template.xml` (für Debugging)

---

## CLI-Interface

### Aufruf-Syntax

```
XfaFlattener <input-pdf> [output-pdf] [Optionen]

XfaFlattener "C:\Formulare\antrag.pdf"
XfaFlattener "C:\Formulare\antrag.pdf" "C:\Output\antrag-flat.pdf"
XfaFlattener "C:\Formulare\antrag.pdf" --dpi 300 --export-xfa
XfaFlattener "C:\Formulare\antrag.pdf" --engine playwright
```

### Parameter

| Parameter | Typ | Standard | Beschreibung |
|---|---|---|---|
| `<input-pdf>` | Pflicht | — | Pfad zur Eingabe-PDF |
| `[output-pdf]` | Optional | `<input>_flat.pdf` | Pfad zur Ausgabe-PDF |
| `--dpi` | int | `200` | Rendering-Auflösung (72–600). Nur relevant bei PDFium-Engine. |
| `--engine` | enum | `auto` | Rendering-Engine: `auto`, `pdfium`, `playwright` |
| `--export-xfa` | flag | false | XFA-XML-Daten vor dem Flattening exportieren |
| `--skip-non-xfa` | flag | false | Nicht-XFA-PDFs überspringen statt kopieren |
| `--overwrite` | flag | false | Existierende Ausgabedatei überschreiben |
| `--verbose` | flag | false | Ausführliche Konsolenausgabe |
| `--chromium-path` | string | (bundled) | Pfad zu Chromium-Installation (falls nicht gebundelt) |
| `--version` | flag | — | Versionsinformation anzeigen |
| `--help` | flag | — | Hilfe anzeigen |

### Exit-Codes

| Code | Bedeutung |
|---|---|
| 0 | Erfolg |
| 1 | Allgemeiner Fehler |
| 2 | Eingabedatei nicht gefunden |
| 3 | Ungültige PDF |
| 4 | XFA-Rendering fehlgeschlagen (alle Engines) |
| 5 | Ausgabedatei konnte nicht geschrieben werden |
| 10 | Kein XFA erkannt (mit --skip-non-xfa) |

---

## Technologie-Stack & Abhängigkeiten

### Freigegebene Libraries

| Library | Version | Lizenz | Verwendung |
|---|---|---|---|
| **PDFSharp 6** | 6.x | MIT | XFA-Erkennung, PDF-Analyse, Metadaten-Extraktion, PDF-Assembly |
| **PDFium** (V8+XFA Build) | Aktuell | Apache-2.0 / BSD-3 | Primäre XFA-Rendering-Engine via P/Invoke |
| **Playwright for .NET** | 1.x | Apache-2.0 | Fallback-Rendering via Chromium Print-to-PDF |
| **System.CommandLine** | - | MIT | CLI-Argument-Parsing |

### Weitere benötigte Libraries (Rückfrage-pflichtig!)

> **WICHTIG:** Folgende Libraries werden voraussichtlich benötigt. Vor der Verwendung muss die Freigabe eingeholt werden!

| Library | Lizenz | Zweck | Status |
|---|---|---|---|
| **PDFiumSharpV2** oder eigene P/Invoke Bindings | MIT | .NET-Wrapper für PDFium-API | ⚠️ Freigabe erforderlich — Alternative: Eigene P/Invoke-Bindings schreiben (kein externes Paket nötig) |

### PDFium-Binary-Beschaffung

**Problem:** Die Standard-Builds von bblanchon/pdfium-binaries enthalten **kein** XFA und kein V8. Für XFA-Support gibt es folgende Optionen:

1. **NuGet: `PDFium.forms.x64.v8-xfa`** (Apache-2.0)
   - Enthält V8 + XFA Support
   - Letztes Update: 2021 (Version 4522.0.6) — **veraltet**
   - Könnte für viele XFA-Formulare reichen, aber neuere PDFium-Bugfixes fehlen

2. **Selbst-Kompilierung von PDFium** (empfohlen für Produktion)
   - Aktuellster Code mit allen Bugfixes
   - Build-Flags: `pdf_enable_xfa=true`, `pdf_enable_v8=true`
   - Build-Dauer: ~1-2 Stunden, erfordert depot_tools und ~30GB Speicher
   - Kann als Build-Pipeline-Step automatisiert werden
   - **Empfohlener Ansatz für das Projekt**

3. **Hybrid:** NuGet-Paket für initiale Entwicklung, später selbst-kompiliert

**Empfehlung:** Für Phase 1 (Prototyp) das NuGet-Paket `PDFium.forms.x64.v8-xfa` verwenden. Für Phase 2 (Produktion) eigenen Build aufsetzen.

### Chromium-Bundling für Offline-Server

Playwright speichert Chromium standardmäßig unter `%LOCALAPPDATA%\ms-playwright`. Für Self-Contained-Deployment:

```
Deployment-Verzeichnis/
├── XfaFlattener.exe              (Self-Contained .NET 8)
├── pdfium.dll                   (PDFium mit V8+XFA)
├── icudtl.dat                   (ICU-Daten für PDFium V8)
├── v8_context_snapshot.bin      (V8 Snapshot, optional)
└── chromium/                    (Playwright Chromium)
    └── chrome-win/
        ├── chrome.exe
        └── ...
```

**Playwright Chromium Bundling:**
- `PLAYWRIGHT_BROWSERS_PATH` Environment-Variable auf `./chromium` setzen
- Beim Build: `playwright install chromium` ausführen und `chromium/`-Ordner mit deployen
- Gesamtgröße: ca. 200-300MB (Chromium) + ca. 50MB (PDFium+V8) + ca. 70MB (.NET Runtime)

---

## Projektstruktur

```
XfaFlattener/
├── src/
│   └── XfaFlatten/
│       ├── Program.cs                    # Entry Point, CLI-Parsing
│       ├── XfaFlatten.csproj             # Projektdatei
│       │
│       ├── Analysis/
│       │   ├── XfaDetector.cs            # XFA-Erkennung in PDFs
│       │   ├── XfaType.cs                # Enum: None, Static, Dynamic, Hybrid
│       │   └── XfaDataExtractor.cs       # XFA-XML-Export
│       │
│       ├── Rendering/
│       │   ├── IRenderEngine.cs          # Interface für Rendering-Engines
│       │   ├── RenderResult.cs           # Ergebnis-Typ (Bitmaps oder PDF-Bytes)
│       │   ├── EngineSelector.cs         # Auto-Auswahl der Engine
│       │   │
│       │   ├── Pdfium/
│       │   │   ├── PdfiumEngine.cs       # PDFium-basiertes XFA-Rendering
│       │   │   ├── PdfiumNative.cs       # P/Invoke-Deklarationen
│       │   │   ├── PdfiumFormFill.cs     # Form Fill Environment Handling
│       │   │   └── PdfiumBitmap.cs       # Bitmap-Verwaltung
│       │   │
│       │   └── Playwright/
│       │       ├── PlaywrightEngine.cs   # Chromium Print-to-PDF Rendering
│       │       └── ChromiumManager.cs    # Chromium-Lifecycle und Pfad-Management
│       │
│       ├── Assembly/
│       │   ├── PdfAssembler.cs           # Neues PDF aus Bitmaps erstellen
│       │   └── MetadataCopier.cs         # Metadaten vom Original übernehmen
│       │
│       ├── Validation/
│       │   ├── RenderValidator.cs        # Prüft Rendering-Ergebnis auf Vollständigkeit
│       │   └── BlankPageDetector.cs      # Erkennt leere/weiße Seiten
│       │
│       └── Infrastructure/
│           ├── ConsoleLogger.cs          # Farbige Konsolenausgabe
│           └── ExitCodes.cs              # Exit-Code-Konstanten
│
├── tests/
│   └── XfaFlatten.Tests/
│       ├── XfaDetectorTests.cs
│       ├── PdfiumEngineTests.cs
│       ├── PlaywrightEngineTests.cs
│       ├── PdfAssemblerTests.cs
│       └── TestData/                     # Test-PDFs (XFA + nicht-XFA)
│
├── samples/
│   ├── XFA-Sample-1.pdf                  # Beispiel XFA Datei 1
│   ├── XFA-Sample-1-flattened.pdf        # Beispiel XFA Datei 1 - breits geflattet zum Vergleich
│   ├── XFA-Sample-2.pdf                  # Beispiel XFA Datei 2
│   └── XFA-Sample-3.pdf                  # Beispiel XFA Datei 3
├── CLAUDE.md                             # Diese Datei
├── README.md                             # Benutzer-Dokumentation
└── XfaFlattener.sln                       # Solution-Datei
```

---

## Implementierungsplan (Phasen)

### Phase 1: Fundament (MVP)
**Ziel:** Einzelne statische XFA-PDFs flattenen

1. Projekt-Setup (.NET 8, Solution, csproj)
2. CLI-Parsing mit System.CommandLine
3. `XfaDetector` implementieren (PDFSharp: `/Root/AcroForm/XFA` prüfen)
4. PDFium P/Invoke Bindings (Kern-Funktionen):
   - `FPDF_InitLibraryWithConfig`
   - `FPDF_LoadDocument` / `FPDF_LoadMemDocument`
   - `FPDF_LoadXFA`
   - `FPDFDOC_InitFormFillEnvironment`
   - `FPDF_GetPageCount`, `FPDF_LoadPage`
   - `FPDF_RenderPageBitmap`
   - `FPDFBitmap_Create`, `FPDFBitmap_GetBuffer`
5. `PdfAssembler` implementieren (PDFSharp: Bitmaps → PDF)
6. End-to-End-Test mit einer einfachen statischen XFA-PDF

### Phase 2: Dynamisches XFA
**Ziel:** Dynamische XFA-Formulare korrekt rendern

1. Form Fill Environment korrekt konfigurieren:
   - `FPDF_FORMFILLINFO` Struct mit Callbacks
   - Timer-Callbacks für JavaScript-Ausführung
   - `FORM_OnAfterLoadPage` aufrufen
   - `FORM_DoDocumentJSAction` und `FORM_DoDocumentOpenAction` aufrufen
2. XFA-spezifische Initialisierung:
   - `FPDF_LoadXFA` nach Dokument-Load aufrufen
   - Warten auf Script-Execution
3. Validierung: Blank-Page-Detection implementieren
4. Tests mit verschiedenen dynamischen XFA-Formularen

### Phase 3: Playwright-Fallback
**Ziel:** Robustheit durch zweite Rendering-Engine

1. Playwright-Integration implementieren
2. Chromium-Bundling und Pfad-Management
3. `EngineSelector` mit Auto-Detection:
   - PDFium versuchen
   - Bei Fehler/leeren Seiten → Playwright
4. `--engine` CLI-Parameter implementieren

### Phase 4: Polish & Deployment
**Ziel:** Produktionsreife

1. XFA-Datenextraktion (`--export-xfa`)
2. Metadaten-Übernahme (Titel, Autor, Erstelldatum etc.)
3. Self-Contained Build mit Chromium-Bundle
4. Umfassende Fehlerbehandlung und -meldungen
5. README.md mit Installationsanleitung
6. Performance-Optimierung (Speicher bei großen PDFs)

---

## PDFium P/Invoke — Kritische API-Funktionen

### Initialisierung

```csharp
// Bibliotheks-Initialisierung mit V8-Konfiguration
[DllImport("pdfium.dll")]
static extern void FPDF_InitLibraryWithConfig(ref FPDF_LIBRARY_CONFIG config);

// XFA laden (MUSS nach FPDF_LoadDocument aufgerufen werden)
[DllImport("pdfium.dll")]
static extern int FPDF_LoadXFA(IntPtr document);
// Rückgabe: 0 = Erfolg, andere = Fehler

// Form Fill Environment (MUSS für XFA-Rendering initialisiert werden)
[DllImport("pdfium.dll")]
static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, ref FPDF_FORMFILLINFO formInfo);
```

### XFA-Rendering-Sequenz (kritische Reihenfolge!)

```
1. FPDF_InitLibraryWithConfig()      — mit V8-Isolate
2. FPDF_LoadDocument()               — PDF laden
3. FPDF_LoadXFA()                    — XFA-Engine initialisieren
4. FPDFDOC_InitFormFillEnvironment() — Form Callbacks registrieren
5. FORM_DoDocumentJSAction()         — Document-Level JavaScript
6. FORM_DoDocumentOpenAction()       — OnOpen Actions
7. Für jede Seite:
   a. FPDF_LoadPage()
   b. FORM_OnAfterLoadPage()         — Page-Level Scripts
   c. FPDFBitmap_Create()
   d. FPDF_RenderPageBitmap()        — MIT FPDF_ANNOT Flag!
   e. FPDF_FFLDraw()                 — Form-Felder zeichnen!
   f. Bitmap-Buffer auslesen
   g. FORM_OnBeforeClosePage()
   h. FPDF_ClosePage()
8. FPDFDOC_ExitFormFillEnvironment()
9. FPDF_CloseDocument()
10. FPDF_DestroyLibrary()
```

> **⚠️ KRITISCH:** `FPDF_FFLDraw()` muss NACH `FPDF_RenderPageBitmap()` aufgerufen werden! Ohne diesen Schritt werden Formularfelder NICHT gerendert. Dies ist der häufigste Fehler bei PDFium-basiertem PDF-Rendering.

### Render-Flags

```csharp
const int FPDF_ANNOT = 0x01;      // Annotationen rendern
const int FPDF_PRINTING = 0x800;   // Druck-Modus (wichtig für XFA!)
const int FPDF_NO_NATIVETEXT = 0x04; // Kein nativer Text-Rendering

// Empfohlene Flags für XFA-Flattening:
int flags = FPDF_ANNOT | FPDF_PRINTING;
```

> **⚠️ WICHTIG:** `FPDF_PRINTING` Flag ist essentiell für XFA! Ohne dieses Flag werden einige XFA-Elemente nicht korrekt gerendert, da XFA zwischen Screen- und Print-Layout unterscheiden kann.

---

## Bekannte Limitationen & Risiken

### XFA-Kompatibilitäts-Matrix

| Feature | PDFium (V8+XFA) | Playwright/Chromium | Anmerkung |
|---|---|---|---|
| Statisches XFA | ✅ Gut | ⚠️ Experimentell | PDFium bevorzugt |
| Dynamisches XFA (einfach) | ✅ Gut | ⚠️ Begrenzt | PDFium bevorzugt |
| Dynamisches XFA (komplex) | ⚠️ Meistens | ❌ Oft fehlerhaft | Validierung nötig |
| FormCalc Scripts | ⚠️ Teilweise | ❌ Nicht unterstützt | PDFium einzige Option |
| JavaScript (XFA-spezifisch) | ✅ Via V8 | ⚠️ Begrenzt | V8-Build Pflicht |
| Dynamic Page Reflow | ⚠️ Teilweise | ❌ Selten korrekt | Schwierigster Fall |
| Barcode-Felder | ⚠️ Teilweise | ❌ Nein | Abhängig von XFA-Version |
| Digital Signatures | ❌ Werden entfernt | ❌ Werden entfernt | Erwartetes Verhalten beim Flattening |
| Verschlüsselte PDFs | ⚠️ Mit Passwort | ⚠️ Mit Passwort | Passwort-Parameter ggf. ergänzen |

### Risiken

1. **PDFium XFA-Binaries veraltet:** Das NuGet-Paket `PDFium.forms.x64.v8-xfa` ist von 2021. Neuere XFA-Bugfixes in PDFium fehlen. → **Mitigation:** Eigenen Build aufsetzen (Phase 2).

2. **Kein 100% XFA-Support:** Keine Open-Source-Lösung unterstützt 100% aller XFA-Features. Adobe Acrobat/Reader ist die einzige vollständige Implementierung. → **Mitigation:** Multi-Engine + Validierung + klare Fehlermeldungen.

3. **Ausgabequalität bei Bitmap-Rendering:** PDFium rendert zu Bitmaps, d.h. Text ist im geflatteten PDF nicht selektierbar. → **Mitigation:** Hohe DPI (200-300), optionale Playwright-Engine für selektierbaren Text, ggf. OCR-Layer als zukünftige Erweiterung.

4. **Speicherverbrauch:** Dynamische XFA-Formulare mit vielen Seiten bei hoher DPI können sehr viel RAM benötigen (300 DPI, A4 = ~30MB pro Seite als Bitmap). → **Mitigation:** Seitenweises Rendering und sofortiges Einbetten, kein Halten aller Bitmaps im Speicher.

5. **Chromium-Bundle-Größe:** ~200-300MB zusätzlich. → **Mitigation:** Optional per `--chromium-path` extern verweisen, oder nur bei Bedarf herunterladen.

---

## Lizenz-Compliance

### Erlaubte Lizenzen
- MIT ✅
- Apache-2.0 ✅
- BSD-2-Clause ✅
- BSD-3-Clause ✅
- Unlicense ✅
- MS-PL ✅

### Verbotene Lizenzen
- AGPL ❌
- GPL (jede Version) ❌ (Vorsicht!)
- Kommerzielle Lizenzen ❌
- SSPL ❌

### Explizit freigegebene Libraries
- PDFSharp 6 (MIT) ✅
- PDFium (Apache-2.0 / BSD-3) ✅
- Playwright for .NET (Apache-2.0) ✅

### Vor Verwendung Rückfrage erforderlich
- **Jede weitere Library** muss vor Einbindung genehmigt werden
- Besonders bei transitiven Abhängigkeiten auf Lizenzen achten
- `dotnet list package --include-transitive` regelmäßig prüfen

---

## Build & Deployment

### Build-Kommandos

```bash
# Development Build
dotnet build src/XfaFlatten/XfaFlatten.csproj

# Release Build (Self-Contained, Single File)
dotnet publish src/XfaFlatten/XfaFlatten.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/

# Playwright Chromium installieren (in Deployment-Verzeichnis)
$env:PLAYWRIGHT_BROWSERS_PATH="./publish/chromium"
pwsh ./publish/playwright.ps1 install chromium
```

### Deployment-Checkliste

- [ ] `XfaFlattener.exe` (Self-Contained)
- [ ] `pdfium.dll` (V8+XFA Build) im selben Verzeichnis
- [ ] `icudtl.dat` (ICU-Daten für V8) im selben Verzeichnis
- [ ] `chromium/` Verzeichnis mit Chromium-Browser
- [ ] `PLAYWRIGHT_BROWSERS_PATH` korrekt gesetzt (oder Default verwenden)
- [ ] Test auf Zielserver mit Beispiel-XFA-PDF

---

## Coding-Konventionen

- **Sprache:** C# 12 (mit .NET 8)
- **Nullable Reference Types:** Aktiviert (`<Nullable>enable</Nullable>`)
- **Implicit Usings:** Aktiviert
- **Fehlerbehandlung:** Exceptions für unerwartete Fehler, Result-Pattern für erwartete Fehler
- **Async:** Nur wo nötig (Playwright ist async, PDFium ist synchron)
- **Logging:** Console.WriteLine für Benutzer-Output, Debug.WriteLine für Entwickler-Output
- **XML-Docs:** Auf allen public Members
- **Encoding:** UTF-8, LF Line Endings

---

## Test-Strategie

### Test-PDFs beschaffen

XFA-Test-PDFs sind schwer zu finden. Quellen:
1. Adobe LiveCycle Designer Samples (verschiedene XFA-Versionen)
2. Selbst erstellte XFA-PDFs mit Adobe LiveCycle Designer (falls verfügbar)
3. Öffentlich verfügbare XFA-Formulare (z.B. US Government Forms, australische SmartForms)
4. PDFium-Testdaten: `testing/resources/` im PDFium-Repository enthält XFA-Testdateien

### Testfälle

| # | Testfall | Erwartung |
|---|---|---|
| T1 | Nicht-XFA PDF | Unverändert kopiert / übersprungen |
| T2 | Statisches XFA (leer) | Geflattete PDF mit leeren Feldern |
| T3 | Statisches XFA (ausgefüllt) | Geflattete PDF mit sichtbaren Daten |
| T4 | Dynamisches XFA (einfach) | Korrekte Seitenanzahl und Layout |
| T5 | Dynamisches XFA mit JavaScript | Berechnete Felder korrekt gerendert |
| T6 | Hybrid-XFA | XFA-Rendering bevorzugt |
| T7 | Verschlüsselte XFA-PDF | Klare Fehlermeldung |
| T8 | Beschädigte PDF | Klare Fehlermeldung, Exit-Code 3 |
| T9 | Sehr große XFA-PDF (100+ Seiten) | Kein Out-of-Memory |
| T10 | DPI-Parameter (72, 150, 300, 600) | Korrekte Ausgabequalität |

---

## Offene Entscheidungen / TODOs

- [ ] **Library-Freigabe:** System.CommandLine (MIT) — Rückfrage beim Auftraggeber
- [ ] **PDFium-Binary-Strategie:** NuGet-Paket vs. Selbst-Kompilierung — Entscheidung nach Prototyp-Phase
- [ ] **OCR-Layer:** Soll ein OCR-Text-Layer über die Bitmap-Seiten gelegt werden? (Würde Tesseract o.ä. erfordern — separate Freigabe nötig)
- [ ] **Batch-Verarbeitung:** Aktuell nicht im Scope, aber CLI-Design erlaubt spätere Erweiterung
- [ ] **Passwort-geschützte PDFs:** Parameter `--password` ergänzen?
- [ ] **PDF/A-Konformität:** Soll die Ausgabe PDF/A-konform sein?
