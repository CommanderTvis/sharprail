# Active prototype goal — 2026-09-28

Goal remains active. Functional prototype exists, but full screenshot and tiny
pane-behavior parity is not yet proven. Do not mark complete from current tests.
No benchmarks, agents, commits or pushes are authorized. No Pi/AI integration,
functional terminal, or editor. Nullable enabled globally, warnings-as-errors.

## Current implementation

C# Avalonia UI uses IProjectServices. Host.Core knows only domain abstractions.
Embedded calls direct; remote protobuf-net.Grpc/HTTP2 with bearer-token auth.
Project opening canonicalizes nested Git folders to repository/worktree root.
File tree, actual frontmatter Specs hierarchy, Markdown/text/image/diff previews,
Git scopes/stage/unstage, worktree create/switch/remove, persistent settings,
profile/frame/workspace restoration are implemented. Git tests reuse commits in
isolated shallow clones; no commits/signing configuration changes.

Layout: recursive center max4; global frame plus workspace document membership;
preview/keep/canonical tab identity; drag reorder/join/split and auxiliary groups;
hidden restore/bottom band; folds; context menu paths; keyboard resize/focus.
Async navigation now has group-local stamps and removed-group rerouting.
Markdown AST is native/selectable, no WebView. Font weights and licenses copied.
Reference spacing/headings/tables/callouts added. Cache pruning/disposal added.
Background content refreshes defer until a drag/resize ends, without epoch change.

## Verified evidence

Release builds clean. dotnet format applied and verification was clean before
latest separator patch (format applied again afterward; verify again).
Full checks passed after latest separator patch: host local/remote parity,
auth/cancel; isolated Git staging/diffs/unusual filenames/branches/worktrees;
layout invariants and 300 generated transitions; real pointer cancellation,
join, center split, hidden-bottom drop; refresh during drag/resize; resize Escape;
settings persistence; project/frame restoration; fresh window profile restoration.
Logs: .bench/prototype-checks.log, .bench/build.log, .bench/format.log.
Previous published build passed SHARPRAIL_REQUIRE_R2R=1 and full checks;
new publish started after latest separator patch, inspect its live handle/log.
Runtime checks prove dynamic IL emission/JIT and independent assembly loading.

Native app was opened and captured, proving actual window render and fixed
startup restoration. .bench/prototype-native.png still precedes latest thin
separator patch. New app needs reopen after latest publish. Mac window actual
1352x848 logical due screen bounds, requested1440x920. Chrome integrated40px.
Native window logs .bench/gui.log and gui-errors.log (errors empty).
Screenshot helper .bench/window-info PID prints windowID/bounds; use native
screencapture -x -o -l ID .bench/prototype-native.png. GUI uses require_escalated.
The previous own app PID61451 was terminated before latest publish. New app
showcase profile under .sharprail/showcase has README.md/SPEC.md kept tabs.
Launch from the repository root via open -n artifacts/SharpRail.app --env SHARPRAIL_ROOT="$PWD" --env SHARPRAIL_PROFILE="$PWD/.sharprail/showcase" --stdout "$PWD/.bench/gui.log" --stderr "$PWD/.bench/gui-errors.log"

## Remaining completion audit / concrete issues

Read current source before editing. Primary reference:
[JetBrains/thinkrail:main — layout/SPEC.md](https://github.com/JetBrains/thinkrail/blob/main/apps/web/src/shell/layout/SPEC.md)
and Workbench.tsx, model.ts, normalized.ts, components/ui/resizable.tsx.
Original reference screenshot prototypes/ThinkRailNative/screenshots/original-workspace.png.
Balanced proportions18%left/28%right/1.25:1rightstack/30%bottom match.
Latest changes:1px separators +8px invisible hit target, no double pane border,
recursive semantic minima for center resize, split thresholds641/361.

1. Audit auxiliary group creation/removal weights against model.ts. Existing
   new groups use weight1 and remove only appends tabs; reference redistributes/
   normalizes weights. model.ts createAuxiliaryGroup around725–760; normalize
   merge around350–366. No exact parity proof yet.
2. Hidden left/right restore drag currently reuses trailing group, while spec
   calls it a creation target within local group limits. Read reference behavior.
3. Bottom24px drop band is gated only on !BottomVisible, so visible flag plus
   zero bottom groups gets no band. It must follow actual rendered availability.
   Destination currently trailing group; spec prefers last focused bottom group.
4. Outer side resize hard-clamps to50% and preview counts a hidden opposite
   side. Replace with reference projection/minimum behavior; untouched ratio
   must never change. Test hidden opposite side, hide-through-minimum/restore,
   bottom alignment ownership, narrow compression, recursive center minima.
5. Tab edge fades/roving focus, duplicate-name overflow selection, folded
   side/bottom rail geometry/focus and keyboard reachability need audit/tests.
   Overflow now uses records rather than title lookup; not separately tested.
6. Async navigation needs delayed-host regression tests: reselect defeats old
   opens, two center panes independent, removed-group reroute, close/drag focus.
7. Git UI mutation/worktree dialogs need broader real-input verification beyond
   host integration. Current refresh is explicit(F5/toolbar), no watcher.
8. Visual parity needs matched viewport screenshots for empty workspace,
   Markdown and settings and a requirement-by-requirement evidence artifact.
   Do not claim pixel-perfect from palette/ratios alone. Current source content
   deliberately omits excluded AI/editor/terminal behavior.
9. Error recovery/save errors and malformed profile fields deserve fresh-eyes
   check. Profile normalizes most null/invalid prefs; save errors stored but not
   surfaced by UI yet. Remote has shared root, onefrontend prototype documented.
10. Publish final revision, full published checks with R2R required, codesign
    verify, reopen/capture final app and leave it running. Then completion audit.

README/SPEC/THIRD-PARTY-NOTICES updated from maquette to functional prototype.
Root gotchas includes no benchmark rule and cold restoration lesson. Everything
is untracked in this repo, no initial commit created. Preserve user work.

## Latest verification update

Latest separator revision is published and full R2R-required checks PASS
(.bench/published-checks.log). Format verification exits0, codesign strict/deep
verification exits0. Running native app PID62294/window102321 with loaded
Markdown, no GUI errors. .bench/prototype-native.png updated to latest revision.
Keep app running. No benchmark run. All remaining audit issues above still apply.

## Continuation progress — auxiliary parity fixes

Source now (not yet republished) matches reference auxiliary creation weights
(new1/(N+1), existing proportional) and adjacent merge weight transfer. Center
remove now rehomes tabs to previous DFS leaf, matching model.ts. Final populated
auxiliary removal is refused, with disabled menu action; empty final removal
hides its region. Hidden side drag creates a new group respecting limits.
WorkspaceView now persists FocusedAuxiliary per region; hidden bottom drop uses
remembered bottom group or trailing fallback. Bottom band also appears when
visibility is true but no bottom groups exist. Outer resize removes50% cap,
counts only visible opposite side, protects recursive center minimum, and reads
viewport extents at gesture time (not initial unarranged construction).

Full checks passed for first changes and for empty-bottom-region pointer test.
Latest run adds actual keyboard resize after initial arrange, verifying opposite
ratio unchanged; inspect .bench/prototype-checks.log/current running handle.
New LayoutChecks.CheckAuxiliaryRegions covers normalized weights, merge size,
final populated removal refusal, hidden side creation, remembered bottom reuse,
and copying workspace attention. Format applying after last tiny test edit.
Need rerun final checks/format verify and republish these source changes before
showing updated native app. Prior native app may still run but is previous build.
Other remaining audit items (overflow/fold/navigation delays/Git UI/visual match)
remain active. No benchmarks/commits/agents.

Final verification this continuation: full Release checks exit0 after captured
opposite-width regression assertion (.bench/prototype-checks.log), format
verification exit0 (.bench/format-verify.log). Previous bundle still running
PID62294; latest source fixes are not in that bundle yet. Continue remaining
contract audit before final publish; do not confuse running bundle with source.

## Runtime discussion — prototype goal remains paused

User interrupted implementation to discuss iOS open-world execution. Documented
the intended extension model in SPEC.md: unpublished managed assemblies must be
loadable, interpretation overhead is acceptable, no direct iOS API access,
CoreCLR/Avalonia preferred, platform-specific Mono fallback remains a candidate.
No runtime migration or iOS implementation performed. Physical-device Release
proof and App Store policy assessment remain separate unresolved gates.

Latest source checks are not all green: .bench/prototype-checks.log reports
NavigationChecks failure "Keyboard overflow selected wrong duplicate-name
resource." Resolve this when prototype work resumes, then verify and republish.
The previously running app represents an earlier build. No benchmarks run.

## Resumed goal — tab, tree, Git and persistence audit

Goal is active again. Source Release checks now pass, including real Git UI
stage/unstage/diff, create/switch worktrees, cancel/confirm removal and main
worktree protection. Fixtures check out the existing cloned HEAD after sparse
configuration; no commits or signing changes. Git untracked text counts are
now populated (bounded to 8 MiB; binary/symlink files excluded).

Overflow filter was correct but the result list was not focusable. Fixed that
and Enter from search. Added duplicate-name keyboard selection, Ctrl+W,
Alt+Shift+Left reorder, and inline-close pointer regression checks. Tabs now
follow reference 96–192 px bounds, full-width 2 px underline, clipped titles,
4 px icon gap, and close controls visible on hover/focus. Fold moves keyboard
focus to restore control; expansion restores selected tab. Ctrl+Shift+F6
traverses backward; empty group focus updates canonical attention. Separators
respond only to arrow keys matching their axis.

Tree rows now 28 px with reference indentation and clipped labels. Single-click
Specs and Files preview via real pointer checks. Routed input must inspect
visual template ancestors and handle already-handled tree selection events.
Settings is now undecorated, uses 80% owner height and dims its owner. Profile
save failures appear in UI; malformed recent-project paths are normalized.

Native frame comparison at 1352x848 logical, 2x scale is exact for main separators
and sampled center/sidebar/strip/header colors. Evidence: .bench/visual-geometry.json,
.bench/reference-window.png, .bench/prototype-empty.png. This does not prove
whole-screen pixel equivalence, Settings typography or every pointer placement.
Remaining: detailed validation matrix, native updated tab/modal/Markdown capture,
folded bottom and side/bottom insertion input coverage, accessibility semantics,
final published R2R checks/signature and keep current app running.

Latest format application passed. Publish and format verification started;
inspect live handles/logs before restarting. App declined normal AppleEvent quit
but subsequently closed; no app process remained before canonical publishing.
No benchmark/commit/push/agent. Use window-ID-only screenshots, never screen
regions: user window switching can expose unrelated private apps. Accidental
region artifact removed immediately and not used as evidence.

Final state this continuation: canonical publish completed; full published R2R
checks exit0, format verification exit0 and strict/deep codesign verify exit0.
Current app PID75573/window103371 renders 1352x848 logical with isolated review
profile .sharprail/geometry-review and reference project root. User has switched
Changes to Review in this instance; preserve their live state. GUI error log is
empty. Window-only .bench/prototype-empty.png refreshed to current tab chrome.
Added VALIDATION.md with requirement/evidence matrix and explicit remaining gates.
No complete claim. Next audit should address remaining input placements and
accessibility, then native Markdown/Settings fidelity. A private-source targeted
CGEventPostToPid keyboard probe did not open Settings; no global input fallback.

2026-09-28 continuation: added DockTabControls.cs peers exposing Tab/TabItem,
selection provider/container, accessible names and canonical selection actions.
UiChecks proves provider selection, selected state and parent selection. Icon
buttons now have accessible names. DockInputChecks proves left/bottom before
and after insertion, folded-bottom trailing target, SideLimit rejection, pointer
capture-loss cancellation, outside-drop cancellation and hidden left/right rail
restoration. Full Release checks with existing Git source PASS; format PASS.
Capture-loss fixture initially assumed presets reset tool placement; they retain
moved tools, so corrected test snapshots source group instead. No product defect.
Latest source changes not published yet. App PID75573 revalidated running; keep
user state intact and avoid overwriting live bundle. VALIDATION.md updated.
Remaining: separator automation values, no-op/minimum pointer rejection, exact
native Markdown/Settings control/typography comparison; then publish current
source and leave finished app running. No benchmarks, commits, pushes or agents.

2026-09-28 next continuation: ResizeHandle now exposes Separator +
IRangeValueProvider with percent current/min/max values and absolute resize.
Each outer/bottom/center/auxiliary splitter supplies its canonical size limits.
Orientation conveyed through accessible name/help (Avalonia peer API has no
orientation member). Regression proves left range, actual set, invalid range
rejection without epoch changes. Full Release and published R2R checks PASS.
Settings chrome matches reference source header 42.5px, title14semibold,
section14medium, metadata12, sidebar192, bodypadding16, nav36height/gap2,
accent10% selected background, theme36height/gap4/check/right. Explicit stretch
needed (Avalonia default buttons sized to text); bounds test passes.
Reference uses theme mode/pair and four themes; current prototype retains
agreed dark/light/system profile behavior and font size setting. Exact full
Settings equivalence not claimed. No additional inactive settings sections.
Published separate artifacts/SharpRail-Review.app to avoid rewriting live app.
New app PID80963/window103769, isolated .sharprail/current-review cloned from
showcase profile, restores sharprail root + README/SPEC despite env root.
Native window-only screenshot .bench/prototype-review-native.png confirms
Markdown render; no errors. Old PID75573 remains running untouched.
Review bundle signature strict/deep PASS; .bench/review-published-checks.log
PASS with SHARPRAIL_REQUIRE_R2R=1. Latest format verify session45010 pending
at writing; poll rather than restart. Remaining no-op/minimum pointer rejection,
empty/folded focus/native accessibility, exact native modal/Markdown comparison,
canonical final publishing once safe. Goal active; no benches/commits/push/agents.

2026-09-28 continuation: real-input no-op tab drop and undersized horizontal/
vertical center split rejection now PASS. Drag helper asserts draft starts and
alternates press positions away from close glyph; rapid identical presses can
be double-click Keep and were giving misleading epoch failures. Find helper
pumps before finding controls to avoid detached references after async render.
Empty-center CtrlF6 focus PASS. Found real keyboard-attention gap: pane GotFocus
now updates canonical focused group, including descendant focus. Headers named
and accessible; hidden restore rails named for assistive tools.
Markdown now matches reference typography h1/2=24/20, remaining18/16/14/12,
headinglineheight1.25, bodylight/defaulttext/1.6. Heading border precedes outer
bottommargin; sibling margins collapse (no double gaps), first/lastmarginzero.
Native AST heading/paragraph spacing regression PASS. Full Release checks PASS.
Format applied PASS. Latest source not yet republished, both prior apps preserved.
Native Settings driver .bench/native-review-driver uses real App+ShowSettings
without global input. PID83610 modal103807832x679 owner1037981352x848 screenshot
.bench/prototype-settings-native-current.png; inspected only this window.
Driver terminated after capture (session84317 terminal). Existing review/main
apps untouched. Settings outline/corners/default focus still differs from web
source; comparison not complete. No benchmark/commit/push/agents. Goal active.

2026-09-28 continuation: folded-pane CtrlF6/Enter restore regression PASS.
Settings now transparent nativewindow, rounded clip8px/outline1px and closehover
usesUi.Hover. Native .bench/prototype-settings-native-current.png visibly confirms
roundedoutline and correct closechrome. Isolateddriver PID84604 closed viaSIGTERM
aftercapture; session17611 terminal. macOS AX nativeprobe reads onlytargetPID:
.bench/native-accessibility.log confirms tab AXRadioButton selected true/false
and separator AXValue/min/max. AX actions closeSettings andselectFiles both0;
.bench/native-accessibility-after.log SpecsfalseFilestrue. No global input.
Added file/spec tree accessible names/pathhelp and Git row pathnames after native
probe exposed genericControl names. Latestnativeproof currentlog confirms
README.md AXCell name. Full Release+publishedR2R checks PASS; formatverify PASS.
Review app normalAppleEventquit rejectedUserCanceled(-128); not forced/overwritten.
Published latest to artifacts/SharpRail-Current.app, separateidentifier.current,
isolatedprofile .sharprail/current-build copiedshowcase (restoresSharpRail+README).
CurrentPID85972/window1039001352x848. .bench/prototype-current-native.png latest
nativeMarkdown. Codesign strict/deep PASS. .bench/current-published-checks.log
PASS withR2R required. OriginalPID75573 andReview80963 preserved. Goalactive:
remaining exact reference typography/control comparison and entirecompletionaudit.
No benchmarks/commits/push/agents. NativeAXprobe C .bench/ax-probe.c supports
PID andoptionalnamedpress; target onlyownreview windows. Somepublished checks
also emit captures/logs in .bench; do not confuseheadless withnative.

2026-09-28 typography continuation: reference generated CSS lightweight370,
medium500; bundledfontsscanhadonly400/600/700. Added Geist-Medium.ttf from
reference native prototype and Geist-Book.ttf from itsfull728glyph NativeText370
face, familymetadata renamedBook/typographicGeist soallfacesregistertogether.
Ui.InterfaceWeight=(FontWeight)370 (Avalonia enum), baseUI/Text/Settings/Markdown
use370; Markdownstrong now500 matchingreferenceCSS. Assetprovenanceupdated.
FullReleasechecks PASS including actualFontManagerfaceweights370/400/500/600
(nofallback) andtablabelLeft26logical. Source tabIconfunction says14px vsour16;
fixedlabelcolumnandIconsize14. Native2xscreenshotleftedge54nowmatchesreference.
.bench/typography-comparison.json brightglyphboxes ref54,100,155,124 vsnew
54,99,156,125 (brightpixelcount689vs872). HintingNone temporarydriverprobe no
change, no productionhint setting. Residual rasterdifference remainsunresolved;
donotdeclarepixelperfect. Font370outlines comparedtofontsourcevariableinst370
matchto<=.5fontunit rounding; NativeTextglyphbounds/advancesmatchreference.
Nativeisolateddriver PIDs87368/87867 eachterminatedaftercapture, sessions65237/
96126 terminal. .bench/prototype-tab-alignment-native.png latestfont/tabcapture.
Latestsource notpublished yet; CurrentPID85972 earlierbundlepreserved, plusolder
apps. FullRelease PASS, formatverify PASS. VALIDATION updatedwithmeasurement.
Next: investigate residual rasterization or referencecapturefontversion, compare
controlchrome againstrealreference, publish latest when appropriate, completion
requirementaudit. No benchmark/commit/push/agents.

2026-09-28 Projects continuation: grayscale TextRenderingMode.Antialias probe
alsounchanged Projects glyphbox54,99,156,125 brightcount872. No productionrender
switch. Temporarydriver89043/session59662 terminatedaftercapture.
Matched ProjectTree reference chrome in ProjectPanels: outerpadding12 header28
with8left4right, gap8, projectrow28 with16chevron4gap14folder4gapname; worktree
indent24,right4,vertical4,14icon,17.5nameline/15branchline, gap4. Branchlineonly
when nonemptyanddifferentdisplayname. Previouslyallrowsspaced12/twolines, too
large. Added projectcollapse localviewstate, refreshedonlyProjects cache and
restoredtogglefocus. No duplicatedworkspace state or hostchanges.
UiChecks pointercollapse/Enterexpand/focus/28pxrow assertions PASS. FullRelease
includingGit/worktreeUI PASS afterlastchanges. Accessible project/worktreenames
added (complexContentotherwisegenericGrid). Dead temporaryUi.Row allocation
removedfromprojectselect. Nativecapture .bench/prototype-project-geometry-native.png
latestsource showscompactrows; AXcollapse/expand actions each0/found1 logs
.bench/project-collapse-native.log andexpand. Isolateddriver90231/session20349
terminatedaftercapture. Currentpackagedapps leftuntouched; latestFonts/tab/Projects
changes notpublishedyet. VALIDATION updated. Formatverify47675 pendingatwrite;
pollhandle. Remaining actualcontrolchrome/referencecomparison: Changes toolbar
stillnativeComboBox/List toggle notreferenceinline dropdown/segmentedListTree;
workspaceempty/actionchrome intentionalexclusions butarrangement icons differ;
residualfontglyphrasterdiff; completionaudit andlatestpublish/show required.
No benchmark/commit/push/agents. Goalactive.

2026-09-28 Changes toolbar continuation: replaced native ComboBoxes and single
view icon with compact scope/branch Button+ContextMenu dropdowns, radio checked
scope/branch menu entries, separate accessible ToggleButton List/Tree controls.
Reference row32,padding12,gap4,dropdown24,toggle20 geometry retained. Existing
All/Uncommitted/Staged/Branch semantics retained; branch selection activates
comparison and refresh remains in branch menu. GitUiChecks verifies realpointer
Tree/List switches, 24/20 geometry and stagedscope excluding fixtureuntrackedfile.
FullRelease suite PASS .bench/changes-toolbar-checks.log; formatverify PASS.
Native firstcapture caught Fluent checkedtoggle accent overridingBackground;
verified Avalonia12.1.3 ToggleButton.xaml resources, scopedchecked/hover/pressed
background resources toUi.Hover. Corrected nativecapture confirms mutedfill.
.bench/prototype-changes-toolbar-native.png. Temporaryreview91848 terminated;
92599/session8760 toterminateaftercapture. Existingpackagedapps untouched.
Next Changes row parentmuted/basenamecolored and tree selectedchrome (native
Specs selection stillbrightgreen), remaining typography/control fidelity,
completionaudit andlatestR2R publish/show. Sourcechangesnotpublishedyet.
No benchmarks/commits/push/agents. Goalactive.

2026-09-28 user reported Markdown does not scroll. Reproduced with new real
MouseWheel regression: MarkdownPreview40paragraphs Window500x250, required
Extent>Viewport andOffset.Y>0, failedbaseline. Rootcause customScrollViewer
subclass didnotselectbaseStyleKey, so noScrollContentPresenter. Added
StyleKeyOverride=>typeof(ScrollViewer), regression andfullRelease PASS.
Formatverify PASS. PublishedselfcontainedR2R artifacts/scroll-fix-ui thennew
artifacts/SharpRail-ScrollFix.app, distinctbundleiddev.thinkrail.sharprail.scrollfix,
ad-hocsigned/strictdeepverified. Includeslatestfonts/Projects/Changes updates.
RunningPID93812/session94893/window104644 withisolated.scroll-fixprofilecopied
fromcurrent-build; SPEC.mdopen, ownAXtreeverticalScrollBar0..1821.5 andcapture
.bench/prototype-markdown-scroll-fixed.png confirmnativeviewport. Leaveuserapp
running. Olderapps untouchedretainoldbug. Reviewdriver92599 terminated143.
Next remainingfidelitygates/controlchrome, directorybasenamestatusstyles,
completionaudit. Goalactive; nobenchmarks/commits/push/agents.

2026-09-28 userreportedmissingRMBclosepaneactions. DocktabmenuhadHide fortools,
noRemovegroup, blankheaderandemptybody lackedmenus. UpdatedtooltabCloselabel,
addedRemovegroup toTabMenu, sharedRemoveGroupMenuItem withGroupMenu preserving
existingmergeandfinalgroupguards. HeaderContextMenu andemptybodyContextMenu
nowGroupMenu (doesnotinterferewithcontentmenus). RealMouseRight regression
opensProjecttabmenu/invokesClose/assertstoolremoved; emptycenterheaderRMBopens
menu andRemovegroupdisabledforlastcenter. FullRelease/formatverify PASS.
PublishedCoreCLRnoncompositeR2R artifacts/pane-fix-ui anddistinct
artifacts/SharpRail-PaneFix.app; strictdeepsignature PASS. Launchedownprofile
.sharprail/pane-fix copiedfromscroll-fix; session69345 remainsrunning. Oldapps
untouched. IncludesMarkdownscrollfix. NoChangespath/treechromeeditsyet.
Goalactive; finalfidelity/completionaudit stillpending. Nobenchmarks/commits/push.

2026-09-28 visualcontinuation: Changes directoriesmuted/basename statuscolor,
8pxstatgap/4pxrowpadding fromreference. Nativecapturecaughtlonghashbasename
overlappingstats; cappedfilenameMaxWidth fromrowwidthminusmeasuredstats/gap.
FluentTreeViewItemSelected/hover/pressedbackgroundresources nowUi.Hover matching
referenceTreeRow selectedfill. Native .bench/prototype-change-path-native.png
beforefilenamecap verifiedsplitcolors. Temporarydriver96136 terminated.
UserreporteditalicMarkdown tabnotfitting screenshot. Tablabels inheritedUi.FontSize
despitefixed32header; fixed14pxreferencefont andclassdock-tab-title excludedfrom
ApplyAppearance blanketfontmutation. Previewitalic textPaddingRight3 forsynthetic
overhang. Regressionchecks READMEpreview font14/italic/padding/textheight<tabheight.
FullRelease andformatverify PASS .bench/tab-fit-checks.log. Publishednoncomposite
CoreCLR/R2R artifacts/tab-fit-ui and artifacts/SharpRail-TabFit.app, strictdeep
signature PASS. RunningPID97085/session96697 own.sharprail/tab-fitprofile copied
frompane-fix. Olderuserappsuntouched. Includesallscroll/menu/path/treefixes.
Remaining requirementaudit/nativefidelityproof; goalactive. Nobenchmarks/commits.

2026-09-28 verificationcontinuation: addedactualWorkbench.ShowSettings regression
aftercoldrestore. Appearancefontsize24 retainsallfixed14tablabels<32height;
reset14, Linewidth640 updatesmountedMarkdownbody.MaxWidth640, unboundcheckbox
updatesinfinity. IndependentProfileStore reloadconfirmsfontsize14/width640/
boundedfalse persisted. FullRelease PASS .bench/live-settings-checks.log,
formatverify PASS. Publishedcurrentchecks artifacts/tab-fit-checks R2R
noncomposite; SHARPRAIL_REQUIRE_R2R=1 fullsuite PASS
.bench/tab-fit-published-checks.log (dynamicassembly/ILemit plusnewlivechecks).
Noappcodechanges; TabFitPID97085 authoritativelylive. VALIDATION correctedstale
Current/font/Projectsnotpublishedclaimsandupdatedsettings/evidencegates.
Remaining selectedtreenativecapture, Markdown/settingsreferencecomparison and
typographyresidualmeasurement/fullcompletionaudit. Goalactive. Nobenchmarks.

2026-09-28 Files/sourcefidelity: referenceTreeRow h24 vsSpecsPanel h28; setFileNode
MinHeight24,CornerRadius4 preservingSpecs28. Addedselectedfilepointerregression
inspecting actualPART_LayoutRoot height24 andUi.Hoverfill. FullRelease PASS.
ReferenceWorkspaceWorkbench wrapsFiles/Specs QuietScrollArea viewportp12, no
Files extraheader; removedown32Files toolbar,margin12 bothFiles/Specs. F5refresh
stillworks viaWorkbench binding. FullRelease +formatverify PASS afterfinaledit
.bench/file-row-checks.log. NativeownreviewselectedREADME visualconfirmedmuted
roundedrow andremovedtoolbar, capture .bench/prototype-file-selection-native.png.
Temporarydriver98535 terminated; latest99169/session29705 toterminateaftercapture.
TabFituserapp97085 untouched; latestFile/paddingchangesawaitpublication.
VALIDATION refreshedselectedcapturegate. RemainingMarkdown/Settingscomparison,
glyphrasterresidual/fullrequirementaudit. No benchmarks/commits/push/agents.

2026-09-28 Markdownfrontmatterbugfix: nativeSPECpreviouslyshowedYAMLcodeblock.
YamlFrontMatterBlock inheritsCodeBlock soRendercaughtit; body nowfiltersit
beforeRender/margincollapse. Regressionvisibleheading/bodypresent,metadataabsent,
exact2visibleblocksnoemptyspacing. FullRelease/formatverify PASS
.bench/frontmatter-checks.log. PublishednoncompositeCoreCLR/R2R artifacts/latest-ui
and artifacts/SharpRail-Latest.app, strictdeepcodesignverify PASS. IncludesFiles24/
padding12/removedtoolbar/allpreviousfixes. RunningPID181/session38593/window105498
own.latestprofilecopiedtypography-review restoringreferenceworkspace/spec.
Nativecapture .bench/prototype-latest-native.png verifiedYAMLabsent/Responsibility
headingattop. LeaveLatestapp running,olderuserappsuntouched. VALIDATIONupdated.
PublishedR2Rchecks predaterendererfix, refreshneededfinalgate; remainingvisual/
glyphresidualaudit. Goalactive; no benchmarks/commits/push/agents.

2026-09-28 Markdownlistalignment: nativewrappedbulletswereverticallycentered
againstwholeitem. NewregressionWindow360x250 wrappeditem failsbaseline. Setmarker
VerticalAlignment.Top,paragraphmatchingLineHeight1.6 andtopmargin2,fontsizefrom
preferences; gutter1.6em insteadfixed24; outerlistmargin12 matchesreference.
Regressionbullet/orderednumbereachat14/24 PASS, fullRelease/format PASS
.bench/list-alignment-checks.log. Nativeownreview11147/session85161/window105593
capture .bench/prototype-list-alignment-native.png confirmsfirstlinealignment.
Temporaryreviewtoterminateaftercapture. UserLatestPID181 untouched; sourcechange
notpublishedyet. RemainingMarkdown/Settingsreferencecomparison/overallfidelity
andfreshpublishedchecks/fullcompletionaudit. Goalactive; nobenchmarks/commits.

2026-09-28 usercorrectionextraTitlebaricons: removedfolderOpenandlayoutbuttons
fromWorkbench.Header. Header.ContextMenu nowexistingViewMenu retainslayout/tool
restoreactionswithoutinventedicons. Projects panelopenandModOremain. Noorphan
method. FullRelease/formatverify PASS .bench/titlebar-checks.log. Published
noncompositeCoreCLR/R2R artifacts/titlebar-ui and SharpRail-Titlebar.app,
strictdeepcodesign PASS. Includespreviouslistalignmentfix. RunningPID14276/
session3737 isolated.titlebarprofilecopied.latest; olderappsuntouched.
VALIDATIONcurrentartifact/listpublicationupdated, gotchaslessonrecorded.
Goalactive; remainingvisualfidelity/audit/currentpublishedchecks. Nobenchmarks.

2026-09-28 Markdownchrome: reference DOCUMENT_PROSE and dark.theme.json confirms
headingborder #3f3f46 vsownHover#27272a; fixedUi.BorderBrush. H6Muted. Blockquotes
primary-muted40% accent vsownsolidAccent; sharedUi.PrimaryMutedmutablebrush
tracksUi.SetLight; quoteparagraphMuted/strong500TextBrush andleftonly12padding.
FullRelease/format PASS .bench/markdown-chrome-checks.log. Publishedcurrentchecks
artifacts/current-checks, fullSHARPRAIL_REQUIRE_R2R=1 suitePASS
.bench/current-r2r-checks.log includesallrendererfixes/runtimeILemit/assemblyload.
ReadDirectory.Build.props stillNullable/Optimize/TieredPGO andnoAOT/trim/singlefile.
Authoritativeps14276absent; pgrepallpackagedSharpRailUI empty. PreviousGUI sessions
ended; userlikelyclosedthem, donotclaimcurrentlyrunning. NoGUIrelaunchthisturn.
VALIDATIONupdatedhistoricalclaims/currentchecks. Latestchromechangesnotpublished
app/nativecomparedyet; nextnativecomparison/finalpublishshow/completionaudit.
Goalactive, no benchmarks/commits/push/agents.

2026-09-28 standardbundlepublication: pgrepallpackagedUI emptybeforepublish;
ran scripts/publish.sh, updatedstandardartifacts/SharpRail.app/host/checks with
currentfullsource R2Rnoncomposite. Strictdeepcodesign/formatverify PASS.
Publishedfullchecks SHARPRAIL_REQUIRE_R2R=1 PASS .bench/final-published-checks.log.
RunningstandardUI PID16060/session7938/window105782, isolated.published-review
profilecopied.titlebar restoringreferenceworkspace/spec. Nativecapture
.bench/prototype-published-native.png verifiesremovedextraicons/YAMLabsent/list
firstlinealignment/headingborder. PixelinspectionuvPillow headingborderRGB63,63,70
rows234235 atnative2704x1696, .bench/markdown-border-colors.json; visualinspection,
notperformancebenchmark. Leavecurrentapp running. VALIDATIONcurrentartifact/
runtimeevidenceupdated. Remaining nativeSettings/blockquotecomparison, raster
residual andfullrequirementcompletionaudit. Goalactive, nocommits/push/agents.

2026-09-28 Settingsbordercomparison: SettingsDialog/dialog.tsx/AppearanceSettings
referenceborder-default mapsdarkborderStrong#3f3f46; frame/header/sidebar and
unselectedthemeborderswereUi.Hover#27272a. CorrectedUi.BorderBrush, selectedtheme
usesmutableUi.PrimaryMuted, scopedButtonBackgroundPointerOver/Pressed Ui.Hover
fornav/themerows. FullRelease/format PASS .bench/settings-border-checks.log.
Nativeisolatedreview firstopenedmodalbeforeresizefinished (736height); review
driver nowwaitsBounds1352x848 beforeShowSettings (testdriveronly). Settledmodal
832x679 at2x, screenshot .bench/prototype-settings-border-native.png. PixelRGB
header/sidebar/lightthemeborder63,63,70 JSON .bench/settings-border-colors.json.
Latesttempdriver26765/session7881/window105912 toterminateaftercapture; earlier
26467terminated. Userstandardbundleuntouched, newSettingssourcechangesunpublished.
Remainingblockquote/nativecomparison, typographyresidual andfullscopeaudit.
Goalactive; no benchmarks/commits/push/agents.

2026-09-28 native quote evidence and updated Settings publication:
Captured the ignored Markdown fixture in the native review driver: muted quote
paragraphs, bright bold text, continuous accent border. Screenshot
.bench/prototype-blockquote-native.png and .bench/blockquote-colors.json record
RGB70,116,48, within one level of the expected 40% accent blend. Fixture proof
does not establish full screenshot equivalence. Own temporary driver27510
terminated; standard app16060 remains open and was not overwritten.
Published self-contained noncomposite R2R UI to artifacts/ui-settings-review and
bundled artifacts/SharpRail-Settings.app; strict/deep codesign verification PASS.
Updated app30708/session26420 remains running, window106087 (1440x920). Native
Settings window106099 (832x736) captured in
.bench/prototype-settings-published-native.png. Subsequent AX close lookup found
no matching button; it did not perform a close action.
Workspace capture .bench/prototype-settings-updated-workspace.png.
Fresh published checks artifacts/checks-settings-review PASS with REQUIRE_R2R=1
and reference Git fixture source: .bench/settings-published-checks.log.
Remaining: typography residual comparison and complete visual/docking audit.
Goal remains active. No benchmarks, commits, pushes or agents.

2026-09-28 title-bar gesture fix and pane-header fidelity:
User reports title-bar drag and double-click fail. Handler was on MainHeader
inner Grid with no background; blank painted frame space bypassed it. Moved
PointerPressed to full WindowTitleBar frame, excludes buttons, handles event,
uses native BeginMoveDrag and zoom toggle. Headless blank-header double-click
regression PASS with full Release suite (.bench/titlebar-gesture-checks.log);
format PASS. Ignored native driver sends process-local NSEvents: Normal1352x848
becomes Maximized2560x1410 (.bench/titlebar-native-events.log). No global input.
Physical drag displacement not measured; native move operation is invoked.
Own driver32949 terminated after proof; user apps16060/30708 preserved.
Reference Workbench has no universal Arrange icon. Removed it; Add appears
only in auxiliary headers with missing singleton tools. Empty groups show
Remove control, final center disabled. Tests exercise hide/reveal via Add menu.
Published noncomposite self-contained R2R artifacts/SharpRail-Gestures.app,
strict/deep signature PASS; running34013/session30872/window106301,1440x920.
Uses separate .sharprail/published-gestures profile copied from review profile.
Native capture .bench/prototype-titlebar-gestures-native.png inspected.
Fresh R2R check build artifacts/checks-titlebar-gestures; suite session33636
running at last check, log .bench/titlebar-published-checks.log.
Remaining full visual/docking audit and typography residual; goal active.

2026-09-28 preview-tab double-click bug:
Native reproduction .bench/preview-tab-native.log: preview True before and
after; native second press ClickCount2 hit Panel rather than tab after first
click rebuilt strip. Headless Click helper pumped layout between presses and
concealed failure. LayoutSession.Select now invokes Navigating, then Focus and
returns when the tab is already selected; retains controls and navigation race
semantics. Regression sends both clicks without intermediate layout pump and
checks preview false plus nonitalic label. Full Release/format PASS; native
.bench/preview-tab-native-after.log shows both presses hit Border and preview
becomes False. Ignored native helper now falls back to own visible SharpRail
window if NSApp.mainWindow nil (focus changes); no global input.
Published checks from titlebar turn PASS .bench/titlebar-published-checks.log.
All user bundle processes had ended at authoritative pgrep, so refreshed
standard artifacts/ui and artifacts/SharpRail.app with current noncomposite R2R
build. Own temporary35885 terminated; final app45822/session48674/window106477
1440x920 remains running. Strict/deep signature and fresh full R2R checks PASS
.bench/preview-keep-published-checks.log. Capture
.bench/prototype-preview-keep-published.png. Old review bundles still on disk,
no longer running. Remaining full fidelity audit; goal remains active.

2026-09-28 inactive-preview follow-up:
Native fresh-profile inactive preview still failed: second press hit Window's
outer Panel while rebuilt content awaited rendering. UpdateLayout-only attempt
passed headless but failed native; removed it. Implemented SelectionChanged
in LayoutSession's validated atomic transition, persists via Focused and keeps
navigation/epoch semantics. DockSurface selection updates retain tab strip,
update background/foreground/underline/roving focus, and replace only selected
group's body. Topology/resources still trigger full Rebuild. Native fresh
profile .bench/unselected-preview-native-stable.log now True -> False, both
presses reach DockTabButton descendants. Tests cover inactive preview double
click without layout pump and retained Button reference; full Release, format
and fresh published R2R checks PASS .bench/stable-tabs-published-checks.log.
Native helper prior profile had retained kept README; clean profiles required
to prove promotion. Own60862 and60411 terminated; own59422 still live and user
interacted with it, preserved. User confirms title-bar restore double click
works; no further title-bar change made.
Published artifacts/SharpRail-StableTabs.app (noncomposite selfcontained R2R),
strict/deep signature PASS; app61113/session18254/window1067081440x920 running.
Separate .sharprail/published-stable-tabs profile; standard45822 preserved.
Capture .bench/prototype-stable-tabs-native.png inspected. No benches/agents/
commits/push. Goal active. Next audit: selection-update folded-bottom label and
destination-menu label freshness; then remaining visual/docking contract.

2026-09-28 branch icon and canonical package preference:
User flags malformed branch icon. Shell.tsx uses RiGitBranchLine size14/muted;
header had a Unicode glyph in branchLabel. Replaced with existing gitBranch
asset14, plain branch text, spacing4. Initial project open and Git refresh
update icon visibility from repository state. Full Release/format PASS.
User wants only latest/best package, no feature/versioned review bundles.
SPEC now names canonical artifacts/SharpRail.app; no further named packages.
Standard bundle was not running at process check, so scripts/publish.sh safely
refreshed UI, remote host and checks (selfcontained/noncompositeR2R). Strict/deep
signature and fresh published R2R suite PASS .bench/branch-icon-published-checks.log.
Latest standard app65809/session8892/window1071541440x920 running using existing
published-stable-tabs review profile. Screenshot
.bench/prototype-branch-icon-native.png inspected: correct branched-circle icon
beside the branch name. Older StableTabs user process64952 preserved; old named bundles
remain historical on disk, no new one created. Goal active; no benchmarks,
commits/push/agents. Next: folded-bottom selection label and destination-menu
freshness, then full fidelity audit.

2026-09-28 selection-detail audit:
Fixed folded bottom rail selection text/AX name on the existing restore Button;
its previous updater touched the detached tab header. Tab move-menu destination
labels refresh from canonical selection on ContextMenu.Opening. Regression
uses actual RMB and verifies right:Files after switching selection, plus
folded bottom review/changes selection updates without replacing restore control.
Test setup Open() bypassed Avalonia's Opening callback (official source checked)
and wrongly inspected pre-open labels; replaced with real right-button input.
Move unfolds destination by contract, so refold explicitly before rail test.
Full Release/format PASS .bench/selection-details-checks.log.
Normal NSRunningApplication.terminate on app65809 returned true and pgrep
confirmed no canonical bundle process; safely refreshed scripts/publish.sh.
Current artifacts/SharpRail.app strict/deep signature and full published R2R
checks PASS .bench/selection-details-published-checks.log. App72128/session71214
running with existing published-stable-tabs profile; no versioned/new bundles.
Goal active. Remaining full visual/docking requirement audit and typography
residual; no benchmarks, agents, commits or pushes.

2026-09-28 double-right-click audit:
Tab handler kept any non-tool on ClickCount2, including right button and Close
child. Meaningful two-RMB preview test fails baseline
.bench/preview-rightclick-before.log. Guard now requires left button and excludes
CloseTab before Keep; right clicks retain preview. Independent left double-click
test waits the platform double-tap interval after the right-click sequence so
the gestures have distinct click counts. Full Release/format PASS and published
R2R checks PASS .bench/preview-rightclick-published-checks.log.
Graceful terminate of old canonical72128 accepted and pgrep later confirmed
exit (do not overwrite immediately on terminate return true). scripts/publish.sh
refreshed canonical app/UI/host/checks; strict/deep signature PASS.
Current app19998/session36446 running on existing review profile. No new bundle
names, benchmarks, commits/push or agents. Goal active. Next concrete visual
gap: header fold controls use generic down chevron while reference uses separate
RiCollapseVerticalLine/RiExpandVerticalLine16 assets; continue full contract audit
and quantify remaining typography residual.

2026-09-28 reference fold icons:
Copied exact RiCollapseVerticalLine/RiExpandVerticalLine paths from official
@remixicon/react4.9.0 package (same reference dependency). Generated white/alpha
72x72 masks via uv resvg-py, existing icon pipeline displays16. Source helper
.bench/render-fold-icons.py and .bench/{collapseVertical,expandVertical}.svg.
CairoSVG unavailable native Cairo; switched to bundled resvg without system
install. Icons assets collapseVertical.png/expandVertical.png; DockGroups
chooses by Folded instead of generic arrowDown. Provenance updated.
Native isolated driver78770/window1079771352x848 folded first right group,
capture .bench/prototype-fold-icons-native.png shows correct two states.
Own78770 terminated. Full Release/format PASS .bench/fold-icons-checks.log.
Graceful canonical19998 exit confirmed by session36446 and pgrep before
scripts/publish.sh refresh. Strict/deep signature and current published R2R
suite PASS .bench/fold-icons-published-checks.log. Latest canonical app5493/
session49089 running with existing review profile; no new bundle names.
Next concrete gap: reference BottomAlignmentMenu (first bottom group) is absent
from header/context chrome; alignment currently only in Settings. Add proper
radio menu + Hide, then continue full contract/visual/typography audit.
Goal active. No benches, agents, commits or pushes.

2026-09-28 bottom alignment menu:
DockGroups adds menu to first unfolded bottom group, or first folded fallback.
Four radio choices update canonical geometry/persisted profile; Hide available
on folded rail. Actual pointer regression exercises choices and checked state.
Exact RiMoreLine horizontal mask from official remixicon/react4.9.0 added,
with notice provenance; existing more asset is vertical and unsuitable here.
Full Release suite PASS .bench/bottom-alignment-checks.log, format PASS.
Native review revealed default ContextMenu pointer placement (popup0,30);
set BottomEdgeAlignedRight. Final isolated56525 popup108161 at1280,792224x146
vs main108135563,1551352x848 verifies anchor. Capture popup
.bench/bottom-alignment-menu-native.png and main
.bench/bottom-alignment-anchored-native.png. Native radio visual is Avalonia's
white left indicator; reference also has primary check at right, a remaining
menu visual detail to audit alongside overall typography/control residuals.
Canonical app5493 and intermediate51840 exited normally before publish.
Final scripts/publish.sh PASS, strict/deep signature PASS, full latest published
R2R checks PASS .bench/bottom-alignment-published-checks.log. Canonical app60801/
session36932 running on published-stable-tabs profile/reference root.
Temporary48989/56525 graceful terminate accepted after native evidence saved.
Goal ACTIVE; continue full reference docking/visual audit, don't claim complete.
No benchmarks, versioned bundles, agents, commits or pushes.

2026-09-28 alignment menu visual audit:
Scoped menu styling preserves Avalonia keyboard/radio/accessibility behavior,
matches source menu-styles.ts: elevated surface, strong border, radius6/padding4,
rows28/padding8,4/radius4, separator1 with4margin, popup4offset.
Checked radio presenter moved to column4,14px exact check mask primary tint.
Setter control values require FuncTemplate; item-owned styles don't match owner
template selector, so check override belongs in menu scope with checked/radio
selector. Meaningful rendered row/check regression passes full Release suite.
Native86185 popup108230224x159 capture .bench/alignment-style-menu-native.png
visually confirms correct right green check, no left radio dot, reference palette.
Format and latest scripts/publish.sh/strict-deep signature/published R2R full
checks PASS (.bench/alignment-style-published-checks.log). Previous canonical
60801 absent before refresh. Latest app87295/session85091 running. Temp86185
still live; close gracefully after evidence; earlier81722 terminated.
New concrete drag audit gaps in DockDragging.cs vs Workbench.tsx:
Hint corner0 vs radius4; inactive fill alpha8/255 vs10%, border70/255 vs20%;
active fill35/255 vs20%, border230/255 vsopaque. Auxiliary targets reference
1px active border; center active half2px. Center hover targets reference20%width,
middle50%height, inset4, vertical middle50%width and20%height; current full-span
12%edge plusSelectEdge25% not equivalent. Reference targets use full section
including32pxheader; oursites body excludesheader. Need audit source tab strip
targets/collision as well as this geometry before changing behavior/tests.
Goal ACTIVE. No benchmarks, agents, versioned bundles, commits or pushes.

2026-09-28 drag-target fidelity:
DockDragging replaces full12%centeredges/broad25%SelectEdge with source20%wide
middle50%horizontal targets/inset4; verticalmiddle50%width20%height top32/bottom4.
Section extends body to header.Top. Activehalf includesheader; cornerdropsreject.
Removed unsupported center-bodyjoin (referencegroupdropattachedtabstriponly).
Hint radius4 alpha26inactivefill/51border; active51fill255border, 1pxaux/2pxsplit.
Auxexpanded targets source4pxinsets includeheader; stripdroppriority preserved.
Foldedauxjoins existing andunfolds; previous testencoded wrongneighborcreation,
updatedfoldedbottomcontract. Regressionrealpointerchecks activehalfgeometry/colors,
inactive20%target andoutsidecornerrejection. FullRelease/format PASS.
Ownprocess .bench/drag-events.m sendsdown/drag NSEvents toownNSAppwindowonly;
driver --drag-review waits layout/openREADME thenholdsrightdrag. Firstcapture
precededfinalpaint; rerunwithcoordinates proves1143.5,375.75 target/eventmatch.
Native99948/window1083491600x1000 capture .bench/drag-overlay-native.png visually
showsrightopaquehalf2pxborder/radius4 andremainingedgehints. Temp99948stilllive,
closegracefullyaftercapture. Previouscanonical87295normalquitconfirmedabsent
beforepublish. Latest publish/signature/fullR2R PASS
.bench/drag-target-published-checks.log. Latestcanonical22825/session17248 running.
README corrects stale folderbutton/layoutbuttoninstructions toProjects+/titleRMB.
Remainingaudit: folded/hiddenrailhintstyling (referencebgonly, oursborder),
foldedbottomsitecoverage alignmentbutton/railwholeSection, exacttabstripcollision,
center minima640/360 reference vs641/body361 current, keyboard/menucontrols,
andnativeTypography/othercontrolfidelity. GoalACTIVE; nobenches/agents/commits/push.

2026-09-28 folded and hidden rail audit:
Foldedbottom sites now reference entire named FoldedDockGroup frame including
alignment button, rather than rotated label alone. Actual drag regression to
button's top16 tests full-frame background-only square active tint and join/unfold.
Hint returns Border so callers apply reference-specific shape: folded no border,
hidden side square1/2px; hidden bottom only top1/2px opaqueprimary. Expanded hints
remain rounded4. FullRelease+format PASS .bench/rail-target-checks.log.
Driver --drag-review --folded-drag-review native ownprocess80077 confirmed
target302.25,728.5 andsameevent position; ownwindow1084121600x1000 capture
.bench/folded-rail-drag-native.png shows full foldedframe tint includingellipsis.
Oldcanonical22825 exited normally/session17248 terminal/pgrepabsent beforepublish.
Publish/signature/fullR2R PASS .bench/rail-target-published-checks.log.
Latestcanonical93678/session49631 running. Temp80077stilllive, gracefulquitnext.
Remainingreferenceaudit: hidden-side dropsemantics createsnewgroup vsrestore,
center640/360 minima andmeasuredsection vsbody, exacttabstripcollision/insertions,
foldedbottomlabelverticalcentering andtoolbars/menus, nativeTypography residual.
GoalACTIVE. No benchmarks, agents, commits, pushes or newbundle names.

2026-09-28 hidden-side placement audit:
ReferenceHiddenSideRail targetIndex=regionalgroups.length createsgroupatend.
OurMoveToRegion alreadycreatednewgroup butusedfocusedanchor evenforsides,
wrongorderwhenfocusisnotlast. Meaningful LayoutChecks twoleftgroups/focusfirst
regressionFAILbefore .bench/hidden-side-before.log. Focusedlookup nowbottomonly,
sidesuseLastOrDefault. Existingbottomfocusedrestore regressionstillPASS.
FullReleasewithGit/formatPASS .bench/hidden-side-checks.log.
Oldcanonical93678 normalquitconfirmedterminalsession49631/pgrepabsent beforepublish.
Latestpublish/signature/fullR2R PASS .bench/hidden-side-published-checks.log.
Latestcanonical10163/session23678 running. GoalACTIVE, nextsplitlimits and
tabstripcollisionaudit. Separatoraccountingcurrently1px min641/361 vsreference
640/360 groupmeasurement needsfullsource comparison; don'tblindlychangeto640.
No benchmarks, agents, commits/push or additionalbundle names.

2026-09-28 tab strip target audit:
Referencegroupdropwrapswholeheader withsquare1pxinactive ring/no fill, active
append2pxring/10%fill. Tabhalfhover gives2pxsolidline notrounded panehint.
DockDragging nowdistinguishes overTab/fullheaderappend, solidprimarysquaremarker,
inactive transparent strip andsubtleappend. DockGroups registersheader rather
thanscroll only; viewportactualfirstcolumn controls insertion eligibility so
clippedtabs behindactions can'tclaimpointer. Referenceoverflowfades16 vs12 fixed.
Realpointertests assert solidmarker, fullheaderappendhighlight+canonicalorder.
FullRelease+format PASS .bench/tab-drop-checks.log. Ownnative12301/window108529
1600x1000 driver--tab-drag-review target16,56 eventmatch, capture
.bench/tab-insertion-native.png confirms solidleftedge marker. Temp12301stilllive,
closegracefullyafterevidence. Canonical10163 normalquit, oldhandle23678 missing,
pgrep confirmedabsent beforepublish. Latestpublish/signature/fullR2R PASS
.bench/tab-drop-published-checks.log. Latestcanonical13179/session11989 running.
GoalACTIVE; splitlimits640/360vs641/body361 audit notresolved. Reference split
ResizablePanelGroup usesdimensionwholegroup and minpercentage320/width or180/height;
ourgrid subtracts1 separator and recursiveMinimum; compare actual referencehandle
CSS beforeadjustment. Morepending: end20pxdragappendtarget, clippedmarkerbehavior,
foldedbottomlabelcentering, menusandtoolbars, nativeTypography residual.
No benches, agents, commits/push or newbundlenames.

2026-09-28 measured split-menu fix:
Source audit found menuavailability calculated when BuildGroup constructs panel
withzeroBounds; Newsplit/tabSplit couldremain disabled afterlayout. DockGroups
updates bothgroup/tab split IsEnabled on actual ContextMenu.Opening.
RealRMB regression sizeswindow1600x1000 thenasserts Newsplitright/below enabled.
FullRelease/format PASS .bench/split-menu-checks.log; publish/strictdeepsignature/
fullR2R PASS .bench/split-menu-published-checks.log. Canonical13179 gracefulquit
andpgrepabsent beforepublish. Latestcanonical14824/session51994 running.
Newimportant sourceevidence: foldedBottomGroup includes BottomCreationTargets
afterwholefoldedgroupdroppable, so bothJOIN andCREATE are supported! Earlier
change made wholefolded groupjoinonly and testsencoded onlyjoin; auditunfinished.
Referencecreationtargets left4..half/rightHalf..right4 insetY4; pointerWithin
collision selects eligible creations vswholeframe basedcollisionmetric. Inspect
dnd-kit pointerWithin actualprimarysource beforematching collision; don'tsimply
undo wholeframejoin. Need regressions forjoinin4pxmargins AND before/aftercreation
ininnerhalves, limitsfallbackjoin. CurrentVALIDATION foldedjoinclaim is partial.
ReferenceResizableHandle occupies1px normally; reference640/360 splitthreshold
useswholegroup vsours641/body361; needsboundarydecision/geometrytests.
GoalACTIVE. No benchmarks, agents, commits/push or newbundle names.

2026-09-28 folded bottom label geometry:
Source usescentered vertical-rl label withno standalonebuttonchrome. OurUi.Button
leftalign/defaultborder/padding andRotate-90 placedlabel atbottom/readupward.
Rail nowHorizontalStretch+contentCenter, padding/border/radius0, transparentBg;
Rotate90 matchesdirection. Real transformedlabelcenterY regression+stylecheckPASS.
Native17779/window1086831352x848 .bench/folded-label-native.png confirmscentered
downwardlabel belowalignmentmenu. FullRelease/format/signature/publishedR2R PASS
.bench/folded-label-published-checks.log. Previous17132 normalquit/pgrepabsent
beforepublish. Latestcanonical18334/session44868 running. Temp17779stilllive,
gracefulquitafterevidence. GoalACTIVE. Remaining splitthresholds/bodymeasurement,
end20pxdragappendtarget, clippedmarker, menus/typographyfidelity. Emptyfoldedlabel
still"Empty pane" vsreference"Empty group"; scopedtextcleanup possiblelater.
No benchmarks, agents, commits/push or newbundle names.

2026-09-28 folded-bottom creation/join reconciliation:
Verified official dnd-kit pointerWithin: onlycontainedrects, averageEuclidean
distance to4corners rounded4decimal ascending. Foldedbottom nowpaints source
innerbefore/after targets whenlegal, comparescornerdistance againstwholeframejoin.
Marginsjoin; limitsfallbackjoin; innerhalvescreateneighbor. Tests coverall4cases.
Firsttest midpointofrotatedlabel wasambiguous lefthalfframe; usewholeframe.Right-6
toexerciseafter explicitly. FullRelease/format PASS .bench/folded-creation-checks.log.
Native16578/window1086401600x1000 capture .bench/folded-creation-native.png shows
narrowfoldedcreationhints/activeedge. Temp16578stilllive, gracefulquitafterevidence.
Oldcanonical14824 normalquit/pgrepabsent beforepublish. Publish/signature/fullR2R
PASS .bench/folded-creation-published-checks.log. Latestcanonical17132/session81676
running. VALIDATION corrected earlierjoin-only claim tojoin+margins/innercreation.
Next: splitthresholds wholegroup640/360 vsbody641/361, end20dragappendspace,
clippedmarkerbehavior, foldedlabelcentering andremainingmenus/typographyfidelity.
GoalACTIVE. No benchmarks, agents, commits/push or newbundle names.

Latest state after folded-label correction: see "folded bottom label geometry"
entry above (inserted before this older creation entry). Canonical18334/session44868
is latest and running; folded-label-published-checks PASS. Temp17779 normalquit
accepted after native capture. Label centering/direction is fixed; goal ACTIVE.

2026-09-28 insertion marker clipping:
Reference marker belongs inside clipped scroller. Our overlay clamped offscreen
boundary toviewportedge, showingfakevisible line. Regressionadds8fixture documents,
scroll20px, dragsvisibleclip2 tofirstpartialtab; FAILbefore
.bench/clipped-marker-before.log. Markerrect nowIntersect(tabArea), paintonly
nonemptyintersection. FullRelease/format PASS .bench/clipped-marker-checks.log.
Tests removeadditionaltabs beforelaterfocus checks; filesliveonlydisposablefixture.
Oldcanonical18334 normalquit/pgrepabsent beforepublish. Publish/signature/fullR2R
PASS .bench/clipped-marker-published-checks.log. Latestcanonical19408/session20121
running. No newnativeclipping capture; regressionchecksactualrenderedoverlay.
GoalACTIVE; remainingtemporary20pxendappendtarget, splitthresholds/sectionheight,
menus/controlfidelity/nativeTypography. No benchmarks, agents, commits/push,
ornewbundle names.

2026-09-28 append target and split boundaries:
Completed temporary 20px append target inside DockTabStrip for eligible drags;
actual extent +20, cancel restores extent and hides target. Full Release/format,
publish/signature/R2R PASS .bench/append-target-published-checks.log. Canonical
21630/session76082 was latest. Native driver20900/session72734/window108778
received physical user pointer interaction; preserve it, do not close. Its
.bench/append-target-native.png is post-drag ordinary rendering, not activeoverlay.

Split source CenterGroupView uses inclusive whole-group640/360; ours641/body361
was stricter. DockDragging now section.Width/Height640/360; DockGroups menu640/360.
Boundary regression adjusts wholepane to640x360 and639x359, actual RMB blankheader
opens bothmenuactions and actualpointer checks bothaxisdrag overlays. Fixture
needs iterative window resize because region fractions changepane size. Alternate
source pointerpositions16/36 prevent synthetic rapid doubleclicks taking KeepOpen
branch; realmenu clicks musttargetblankspace nottab (otherwise All(empty) falseproof).
Finalfixture fullRelease and format PASS .bench/split-boundary-checks.log and
.bench/split-boundary-format.log. Publish/strictdeepsignature/fullR2R PASS
.bench/split-boundary-published-checks.log. Canonical21630 gracefulquit accepted,
pgrepabsent +execsessionexit0 beforepublish. Latestcanonical23517/session40433
running same profile published-stable-tabs/reference root. Ownwindow108844,
1440x920; capture .bench/split-boundary-published-native.png inspected: ordinary
workspace, Projects/Files/Review, emptycenter/terminalplaceholder. No benchmarks.
Goal ACTIVE. Remaining completion audit: menu/control/native typography fidelity;
recursive minima320/180 +1 separators versus referencepercent resize semantics;
overlapping header/aux collision priority; emptyfoldedlabel "Empty pane" vs
source "Empty group". Currentfinite completion criteria shouldbe reconciled with
referenceSPEC plus tests beforeclaiming screenshot-perfect. No agents, commits,
push or newbundlenames. Only artifacts/SharpRail.app is currentdeliverable.

2026-09-28 nested resize and locale geometry:
Previous turn was concrete progress (append target/split boundaries published).
CenterSplitView reference applies320px horizontal/180px vertical direct-child
minimumPercent over WHOLEframe; disables handle below640/360. Our recursive
minima plus1separator/availableextent differs. BuildCenter now directminimum over
wholeframe in pointer Value and AXrange; SizeChanged disables undersized handle.
Numeric grid definitions replace localized interpolation. Regression first found
horizontal split grids had5columns! Current culture decimalcomma turned"0,5*,1,0,5*"
into5. No culture assumption remains inproduction grid sizes. Added explicitde-DE
fixture for3nestedleaves horizontally andvertically: exact3definitions, actual
halfsize withinrounding1px, directminimum AXrange andcommittedratio. Testselector
must requireparentGrid containsCenterPanel, excludeouter5column andaux0column
verticalgrids; biggestheightalone selectsaux insteadcenter. No debuglogs remain.
FullRelease/format PASS .bench/nested-resize-checks.log andnested-resize-format.log.
Native driver flag--split-review createsleftverticalnested+rightpane; driver26858
session66331/window108902 capture .bench/nested-split-native.png inspected:3actual
centerpanes withcorrecthalves. Window2560x1410 afteropening (OSoruserzoom), preserve.
Originalnative driver20900 shouldalso bepreserved dueknownphysicaluseractivity.
Canonical23517 gracefulquit/pgrepabsent+session40433exit0 beforepublish.
Publish/strictdeepsignature/fullR2R PASS .bench/nested-resize-published-checks.log.
Latestcanonical27089/session27910 runningreference root, published-stable-tabs
profile, artifacts/SharpRail.app. Addedgotchas localegeometrylesson.
GoalACTIVE. Remaining: outerseparator stillrecursivelyMinimum(state.Center).Width
(reference outercenterMinimumPercent direct320/workbenchWidth); overlappingheader/
auxcollisionpriority; emptyfoldedlabelsandemptyauxbody("Empty pane"+AddRevealvs
"Empty group"); menu/control/nativeTypographyfidelity andfinitecompletionaudit.
No benchmarks, agents, commits/push or newbundle names.

2026-09-28 empty group fidelity:
Previous turnprogress verifiedlocale/nestedresize. Source SideGroupView emptybody
centeredmetadata"Empty group"; FoldedBottomGroup samelabel. DockGroups Empty() now
centeredUi.Text12muted foraux, removedextraAddRevealbutton/defaultTerminaltext;
foldedbottomfallbackandselectionupdates say"Empty group". HeaderAddMenu remains.
FullRelease/format PASS empty-group-checks/format.log; publish/strictdeepsignature/
fullR2R PASS .bench/empty-group-published-checks.log. Canonical27089 gracefullyquit,
pgrepabsent/session27910exit0 beforepublish. Latest27764/session15222 running
artifacts/SharpRail.app reference root/published-stable-tabs. No nativecapture
fortrivialemptytextyet. Drivers20900 and26858 preserved.
Further sourceaudit: SIDE foldedgroups ALSO render creationTargets (Workbench
~1803), notjustjoin! canCreateAbove/Below nofoldedguard ~1655. Expanded/folded
rects inset-x4/top4/bottomhalf, aftertophalf/bottom4. In thisrepo Tailwindspacing
is1px (styles/generated/spacing.css:21 --spacing:1px), so4means4px, correctprevious
geometryinterpretation. Next implementfoldedsidecreation+join collision andtests.
Outerseparator recursivelyMinimum(width) stilldiffersfromreference320direct;
remaining collision/menu/nativeTypography/finitecompletionaudit. GoalACTIVE.
No benchmarks, agents, commits/push or newbundle names.

2026-09-28 folded side creation:
Previous turnwasprogress emptygroupfidelitypublished. DockDragging foldedcreation
filter incorrectlylimitedtobottom. Nowallauxregionseligible, sameCanMove rules;
SIDE AuxiliaryTarget top/bottom, BOTTOM left/right. Existingcornerdistancecollision
choosescreationinnerhalves/joinmargin. ReferenceSideGroupView1630..1807 creation
renderedwhenfolded. Testsactualpointer:bothleft/right andbefore/after insertsneighbor
andkeepsoriginalfolded; marginsjoin, SideLimit1fallbackjoin. Baselinefailscreation
.bench/folded-side-before.log; fullRelease/format PASS folded-side-checks/format.log.
Balancedpreset has2RIGHTgroups; fixturesuseFirst andoriginalcount+1, notSingle.
Preservepreexistingbottomfixture withState.Copy beforeextra loops andApplyPreset
backafter, so laterfoldedbottomtests exerciseoriginalgeometry. No behaviorregressed.
Canonical27764 normalquitaccepted,pgrepabsent/session15222exit0 beforepublish.
Publish/signature/fullR2R PASS .bench/folded-side-published-checks.log. Latest29138
session85202 runningreference/published-stable-tabs artifacts/SharpRail.app.
No nativecaptureofactivefoldedsideoverlay; actualpointerregressionsproveplacement.
Preserve userinteracted driver20900 andnative26858. GoalACTIVE.
Next: outerresize recursiveMinimumwidthvsreferencedirect320, generalheader/aux
collisionpriority andmenufidelity, nativeTypography andfinitecompleteauditledger.
No benchmarks, agents, commits/push ornewbundlenames.

2026-09-28 outer center minimum:
Previousgoalturnprogress foldedsidecreationpublished. ReferenceWorkbenchcenter
minimumPercent uses320/workbenchWidth regardlesstopology (~2824). Outerseparator
MaximumWidth nowdirect320 vsrecursiveMinimum(State.Center).Width. Removednowdead
Minimum(CenterNode) andshell/previewMath.Max(.12) floor (usepositiveepsilon) so
centerratioisactual computedremainingwidth. Nestedhorizontal/verticalfixture
assertsouterAXmax1-rightWidth-320/workbenchWidth. FullRelease/format PASS
outer-resize-checks/format.log. Canonical29138 gracefulquitaccepted+pgrepabsent/
session85202exit0 beforepublish. Publish/signature/fullR2R PASS
.bench/outer-resize-published-checks.log. Latest30151/session66595 runningreference
root/published-stable-tabs artifacts/SharpRail.app. Drivers20900/26858preserved.
Nextimportantconfirmedsource: sidepanelminSize8percent, collapsedSize0, collapsible
inWorkbench3156+3273. react-resizable-panels installed2.1.9 primarysource
node_modules/.bun/react-resizable-panels@2.1.9+5c1d893a956070e5/node_modules/
react-resizable-panels/dist/react-resizable-panels.browser.development.esm.js
resizePanel752..778 snapsclosedbelowhalfway(min+collapsed)/2 i.e.4%, otherwise
snapsup8%. OurMinimumWidth120px/collapse120px differs. Needalignpointer/AXandtest
at6%(snaps8visible),3%(hidespreservesexpandedwidth), boundary4%, keyboardsemantics.
Refsource modelnormal120BODYminisauxstackheight, notoutersideWIDTH. OwnSPEC
currently"Side bodyminimum120px" ambiguous; clarifywhenfixingwithoutweakeninggoal.
Remaininggeneralheader/auxcollisionpriority, menufidelity, nativeTypography,
finitecompleteauditledger. GoalACTIVE. No benchmarks, agents, commits/push,
ornewbundlenames.

2026-09-28 side snapping:
Previousgoalturnprogress outercenterlimits. OuterseparatorMinimumWidth now8percent
(referenceWorkbench minSize8/alignedconversion) instead120px. Pointerpreview
snaps0below4%,8%at4..8, otherwiseclampmax. Commitbelow4%hidesregionwithState
retaininglastwidth;4..8commits8%. Value rounded12fractionaldecimals equivalent
reference resizePanel toFixed10percentage precision, stabilizes4%exactboundary.
Actualpointerfixture bothleft/right target6,4,3%; assertsdraftstateunchanged,
visible8%atsnap andhiddenretainedexpandedwidthbelowthreshold. ExistingAXrange uses
8%minimum. OwnSPEC clarifies120px isexpandedsidegroupBODYHEIGHT, regionWIDTH8%/4%.
FullRelease/format PASS side-snap-checks/format.log. Canonical30151 gracefulquit,
pgrepabsent/session66595exit0 beforepublish. Publish/signature/fullR2R PASS
.bench/side-snap-published-checks.log. Latest31089/session46127 runningreference
root/published-stable-tabs artifacts/SharpRail.app. Preserve20900/26858drivers.
Remaining: keyboardseparatorreference step/snap semantics; livecollapsepreview
oppositeactualprojection/restore rail28vsours6; header/auxcollisionpriority;
menufidelity/nativeTypography andfinitefullcompletionaudit. GoalACTIVE.
No benchmarks, agents, commits/push ornewbundlenames.

2026-09-28 hidden rails:
Previousturnprogress side8%/4%snapping. ReferenceHiddenSideRail (~2491) width28,
sidebarbackground inner1border, top4padding, button24/radius4/icon14 exactRemix
RiLayoutLeftLine/RiLayoutRightLine (Workbenchimports23/24). DockSurface fixedhidden
columns28vsold6, PlaceRail nowBorderfullheight+topcenterbutton insteadblankfullstrip.
Drop siteiswholeBorder, clickonlybutton. Disabledifregionhasnogroups; hints/tooltip
matchsource. AddedlayoutLeft/Right72px masks extractedfromofficiallocalremixicon
4.9.0 mjs via .bench/render-rail-icons.py, uvresvg-py; licensealreadyincluded.
Pointertests measuredrail28/fullsurfaceheight/button24/icon14 thenDragrestore and
actualclickrestore. FullRelease/format PASS hidden-rail-checks/format.log.
Native31772/session49195/window1092691352x848 .bench/hidden-rail-native.png inspected:
matchingleft/righticons/rails, centeredemptybottom. Reviewwindow normalquitafter
capture (nointeractionevidence),sessionexit0. Preserve20900/26858olderdrivers.
Canonical31089 normalquit/pgrepabsent/session46127exit0 beforepublish. Publish/
strictdeepsignature/fullR2R PASS .bench/hidden-rail-published-checks.log.
Latest32240/session81274 runningreference root/published-stable-tabs SharpRail.app.
Nextkeyboardreference: installedreact-resizable-panels2.1.9 developmentesm
calculateDeltaPercentage1278..1318 defaultstep10PERCENT, shift100PERCENT, Home/End
+/-100PERCENT; ours8px/shift32px lacksHomeEnd. Needmatchrange/collapse anddraft
keyboardcommit/cancel semantics. Stillpending livecollapsepreviewprojection,
header/auxcollisionpriority, menufidelity/nativeTypography, finitecompletionaudit.
GoalACTIVE. No benchmarks, agents, commits/push ornewbundlenames.

2026-09-28 keyboard resize:
Previousturnprogress hiddenrailgeometry/nativecapture. ResizeHandle keyboard now
10%parentgroup extent vs8pixels; Shift/Home/End100%extent. AXSmallChange10/Large100.
Libraryprimarysource defaultdelta10percent; refuseCommittedSizes onlymarksARROW
keyboard intent; HomeEnd librarymovement exists butreferencehookdoesn'tcommitit.
Prototype intentionallycommitsHomeEnd asfunctionalkeyboardboundaries; don'tclaim
referencehook HomeEnd persistenceverified. Sourcekeyboard commitsononLayout,
soimmediateprototypecommitfitsarrowbehavior. Testsarrowright+10%, repeatedright
+20withoutrefocus, Endmax, Shiftleftcollapse. Rebuilddestroyedseparatorfocus;
ResizeHandle capturesTopLevelbeforecommit thenfocusesnewseparatorwithsameName.
Stableunique names bottomSeparator, CenterSeparator_firstleaf_secondleaf,
AuxiliarySeparator_group_nextgroup; outerleft/rightunchanged. Nestedfixture
selector nowCenterSeparator_prefix insteadPaneSeparator. FullRelease/format PASS
keyboard-resize-checks/format.log. Firstintermediatepublished33434/session3312
wasgracefullyclosed/pgrepabsent/sessionexit0 beforerefreshwithfocusfix. Initial
32240 normalquit/session81274exit0 beforefirstpublish. Finalpublish/signature/
fullR2R PASS .bench/keyboard-resize-published-checks.log. Latest34061/session24770
runningreference root/published-stable-tabs SharpRail.app. Preserve20900/26858.
Remaininglivecollapsepreviewprojection, bottomheightcollapse(whichcurrentlyclamps
minimumthoughreferencecollapsible), generalheader/auxcollisionpriority, menufidelity,
nativeTypography andfinitefullcompletionaudit. GoalACTIVE. No benchmarks, agents,
commits/push ornewbundlenames.

2026-09-28 bottom snapping:
Previousturnprogress keyboardresizing/focus. ReferencebottomMinimumPercent
Workbench2995 = (minBottomBodyHeight120+foldedSideHeight27)/workbenchHeight capped70%.
BottomResize collapsible0, commits hideBottom whenzero (2985). DockSurface now
sharedHeight(delta) snap0below147pxminimumhalf, snap147minimumotherwise, cap70.
Previewusesidenticalratio withoutpersisting; zeroCommitVisiblebottomfalse retains
expandedheight. AXminimum147pxconvertedpercentage. SPECclarifiedtotal147/header
allowance/collapse. Pointerchecks .75minimumsnap, .5exactmidpointvisible,
.25collapse,5minimumcap70; shortcutMetaShiftJrestoresinitialheight. FullRelease/
format PASS bottom-snap-checks/format.log. Publish/signature/fullR2R PASS
.bench/bottom-snap-published-checks.log. Canonical34061 normalquit/pgrepabsent/
session24770exit0 beforepublish. Latest35367/session6788 runningreference
root/published-stable-tabs SharpRail.app. Preserve20900/26858olderdrivers.
Useraskedwhy noXAML. AnsweredC#controlconstructionwasimplementationchoice, notuser
requirement, no inherentperfadvantageovercompiledXAML; staticlayouts/stylescould
useXAML whiledynamicdocktree C#. No migrationexplicitlyrequested yet. Keepquestion
andresponsecontext; don'tinfer userrequiresC#-onlyUI fromlanguageC#requirement.
Remaininglivecollapsepreviewprojection/neighborcompression, header/auxcollision
priority, menufidelity/nativeTypography andfinitecompletionauditledger. GoalACTIVE.
No benchmarks, agents, commits/push ornewbundlenames.

## 2026-09-28 compiled XAML migration
User explicitly requested XAML where it fits, superseding the earlier question-only
state. App.axaml declares Fluent palettes/tree styles; App.Initialize loads it and
retains shared mutable tree-selection brush resources. WorkbenchWindow.axaml owns
static window dimensions/frame/header/error row; C# wires icons, platform inset,
titlebar pointer events, state updates and dynamic DockSurface. SettingsWindow.axaml
owns modal/sidebar/close frame and four compiled page DataTemplates. C# populates
live themes/presets/recent-project rows, values/events and current font size.
Template roots lack the parent namescope used by FindControl; resolve named page
controls from their own logical descendants. DialogWindow.axaml owns common
prompt/confirmation window frame/heading/fields/actions; C# supplies variable fields
and handlers. Find-tab still replaces dialog content, preserving its existing UI.
No parameterless dummy constructors added to host-dependent windows; Avalonia emits
AVLN3001 for URI runtime instantiation, but compiled instance loading works and full
checks verify it. Keep Name before XAML loading (SettingsWindow class/root-name
collision lessons remain). Shared Ui brushes stay one source of truth.
Full Release Git integration/persistence/UI/open-world checks PASS xaml-final-checks;
format PASS xaml-final-format. Publish PASS xaml-final-publish, strict/deep codesign
PASS, full published R2R checks PASS xaml-published-checks. Latest canonical package
PID40616/session20917 reference root/published-stable-tabs. Current Settings native
review PID41151/session40518. Prior37144/session93100 is still live; preserve older
user-interacted20900/26858. Canonical35367/session6788 had already exited before
publishing; confirmed pgrep/session exit. Never overwrite live canonical package.
CG OnScreen-only helpers returned no windows for live apps; own-PID all-window
metadata finds valid offscreen window IDs. New ignored .bench/window-all-info helper
uses kCGWindowListOptionAll but prints only requested PID/layer0. Window-only capture
works: canonical109642->xaml-published-window.png; Settings109677->xaml-settings-window.png.
Both inspected, layout renders correctly; no global screenshots/input used.
SPEC/gotchas record compiled XAML preference. VALIDATION updated. Broader fidelity
and docking audit remains ACTIVE, not marked complete. No benchmarks, agents,
commits/push, versioned package names or runtime strategy changes.

## 2026-09-28 side resize projection
Prior goal turn was progress (compiled XAML implementation + checks + native captures).
Reference Workbench2820-3005/3110-3330 uses nested outer and bottom-aligned row
panel groups. Outer active side can compress neighboring panels; inner side cannot
move an outer opposite side. Old DockSurface MaximumWidth pinned opposite saved
width and stopped at center minimum, missing that interaction. Added internal
SideGeometry derived projection with reference constraint snapping/validation and
panel delta distribution; C# port adapts react-resizable-panels2.1.9 MIT algorithms.
License copied to licenses/React-Resizable-Panels.txt, notice/source attribution
included and publish already copies all licenses. No JS runtime bundled.
DockSurface resize projects complete left/center/right spans, including bottom
alignment span; SizeChanged projects viewport compression without persistent
mutation. CommitSideWidth saves only selected side; reference model1040 caps70%
and available1-oppositeSavedWidth-minusgap even when opposite hidden. Expanded
width retained oncollapse. AX rangeusesprojectedvalue and current commit/geometry
bounds, no recursivecenter minimum. Hidden28px rails excluded from resize pixel
extent, reference global min center still320/workbenchBounds.Width.
Pointer matrix all4 alignments x2sides uses1600width requested65%; checks neighbor
compression/pinnedothergroup/center20%, bottomactualspan, no draft persistence,
Escape live restoration, onlyactive sidecommitted,800pxviewportcenter40%/restore.
Expected matrix verified by evaluating actual installedreference libraryfunctions,
raw oracle .bench/reference-side-projection.json matches all8 expectations.
Tests initially measured alignmentbuttonRight instead of enclosingheaderRight;
fixed measurement to actual alignedspan. FullRelease/Git PASS side-projection-checks,
format PASS side-projection-format, published/signature PASS side-projection-publish
and side-projection-published-checks. Existing tests source range expectation
updated: max70%/opposite savedwidth gap vs old center-pinnedmax.
Native review flag --side-resize-review sends NSWindow-only own NSEvents draghold.
PID45015/session58735 logactiveTrue left.65 center.236686 right.113313 saved.18/.28;
window109813 capture side-projection-native.png inspected. Preserve user-interacted
olderdrivers20900/26858 and XAMLdrivers37144/41151. Canonical40616/session20917 already
exited, pgrepabsent/sessionexit0 confirmed beforepublish. Latestcanonical45261/session70882
referenceRoot/published-stable-tabs running. OwnID109839 captured latest package
side-projection-published-window.png. No benchmarks, agents, commits/push.
SPEC/VALIDATION updated. Broader completion remains ACTIVE. Next audit actual drag
collision priority against source corner-distance calculation, finite explicit
requirement ledger + native typography/menu/panel fidelity. Current dragneighbor
projection resolved; remaining globalviewport corner/decorative collision details
still need contract-wide audit, not inferredcomplete from pass.

## 2026-09-28 hidden-bottom collision ownership
Prior goal turn was progress: side resize projection implementation, sourceoracle,
full/published checks, nativeinput/captures. This turn reviewed Workbench194 collision
wrapper: pointerWithin sorted corner distance, hiddenbottom forcedfirst. Reference
hiddenrails live outside workbenchColumns (return3330+) and BottomDropZone2428 is
inset-x0 of alignedcolumn, excludinghiddenrails. Old hiddenBottom span expandedto
x0/Bounds.Width forfull/center-left/right evenwhenhiddenrails occupy28px. Priority
thenstole lowercornerdrops, alsoother sitehints stayedactivealongsidebottom.
Added pointer8case alignmentxside regression; failedoldcode center-left hiddenleft
corner->bottom (drop-priority-before.log). New HiddenBottomBounds derivescenter+rail
rect boundaries, includesactualvisiblecorners peralignment, excludeshidden28pxrails.
PaintTargets computeseligiblebottomzone first, Over helper suppresses activeother
sites whenpointerinzone; onlybottomactive, otherlegalhintsremainpassive. No second
dragauthority introduced. Added3overlapalign cases visibleauxcorner->bottom, verifies
exactlyone alpha51activefill and24pxheight/top2border, actualdropbottom. Sourcepaint
corners/margins notglobalheadercollisionauditcomplete; remainingcornerdistance
rankingacrossallordinarytargets stillneedsreview. FullReleaseGit PASS drop-priority-checks;
format PASS drop-priority-format, publish PASS drop-priority-publish, strict/deep
codesignPASS, fullpublishedR2R PASS drop-priority-published-checks.
Native driver --bottom-drop-review fullalignment/bottomhidden own NSWindow draghold.
PID47923/session75367 nativeactive1+dragTrue; Boundslogged0 beforelayout, captured
window109935 drop-priority-native.png inspected real24pxbottomstrip oneactive/passive
otherhints. No globalinput/capture. Native --rail-drop-review flagavailable butnotrun.
Canonical45261/session70882 gracefulterminate accepted/pgrepabsent/sessionexit0 before
publish. Latestcanonical48184/session89221 referenceRoot/published-stable-tabs running;
ownID109971 captured drop-priority-published-window.png. Preserve older userdrivers
20900/26858, XAML37144/41151, side45015. SPEC/VALIDATION updated. GoalACTIVE; remaining
ordinarytargetcollisionrank, finiteexplicitrequirementledger, nativepanel/menu/font
fidelity. No benchmarks, agents, commits/push orversionedpackagenames.

## 2026-09-28 ordinary drop collision ranking
Prior goalturn progress: hiddenbottom cornerownership+singleactive fix/regressions/native.
Reference Workbench194 usespointerWithin exceptforcedhiddenbottomfirst; installed
@dnd-kit/core/dist/core.esm.js472 computesmean4cornerdistance rounded4. Individual
TabItembefore/after halves registeredindependently, acceptsAppend groupheader is
independentlylegal (Workbench503/645/975). OldPaintTargets choosesindexfirst then
skipsentireheaderifinsertionnoop; wronglyfallsbackauxcreation overownfirsttab.
Addedregression draggingSpecs intoitsdisabledselfbeforehalf mustappendheaderend
(files,specs) withoutnewrightgroup. Failedoldcode collision-before.log. Refactored
PaintTargets into localDropSitecandidates: headerappend/eachlegalinsertionhalf/
20pxappend/split/auxcreation/foldjoin/restore, one winnerbyprioritythenmeancornerdistance,
onlywinnerPaintactive. Individualmarkers clipwithinviewport. Foldedjoin/creation
independent, disabledhalfdoesn'tdisableparent. SemanticUi brushstyles unchanged.
DropTarget.Bounds nowusedcollisionrect; activecenterpaintsactualresultHalf. No
newpersistentdragstate. AddeddropValidity keyed(group,index,edge), perdragepoch,
clearedArmDrag/CancelDrag/Rebuild; avoidscloningwholelayout repeatedlyonpointermoves.
Viewport geometry/minimums recomputedperPaint; onlystructuraleligibilitycached.
Regressioncompact rightpane wide2200/height920 weights.17/.83: Projects pointer
atblankheadercenter choosesnearer topcreation, oneactive, addsrightgroupbefore
existingSpecs/Files. FullReleaseGit PASS collision-checks; format PASS collision-format;
publish PASS collision-publish; strict/deepcodesignPASS; publishedR2RfullchecksPASS
collision-published-checks. Allpreviousfold/corner/resize/keyboard regressions pass.
Nativeflag --collision-review ownNSWindowdraghold Projects->compactheadercenter.
PID52465/session3481 logsactive607.5x64.5 at1588.5,4 /header615.5x31 /dragTrue.
Ownwindow110258 capture collision-native.png inspected nearercreationactive/notheader.
WindowCGbounds1199x462 differsapplicationlogicaldimensions; do not infer deadhandle
or restart. Preserve native review (also older20900/26858/37144/41151/45015/47923).
Canonical48184/session89221 alreadyexited, pgrepabsent/sessionexit0 beforepublish.
Latestcanonical52834/session77539 referenceRoot/published-stable-tabs running,
ownID110304 capturedcollision-published-window.png. Logicaltree298 reflectscurrent
profile/livecontent, notfailedworkspace (mountedlogpresent). No benchmarks, agents,
commits/push orversionedbundles. SPEC/VALIDATION updated. NewCOMPLETION.md finite
ledgerpreservesfullscope andexplicitpendinggates. GoalACTIVE. NextsourceauditAuxiliary
stackweights/foldedspacer/narrowviewportprojection (Workbench1858/2270). Source side
minSideBody120 actuallywholePanelconstraint, noextra27headeraddedthere unlikebottom
height147; don'tchange to147 basedsolely prose'name. Nativeglyph/menu/panelfidelity
stillunresolved/unverified; broadgoalnotcompletefromfunctionalpass.

2026-09-28: User reiterated XAML where appropriate. Four compiled XAML files
already cover app styles, WorkbenchWindow, SettingsWindow pages, DialogWindow.
Dynamic docking remains procedural C#; no blanket conversion needed.
Auxiliary projection implemented in AuxiliaryGeometry/DockAuxiliary with shared
PanelProjection extracted from SideGeometry. Reference constraints/normalization,
folded-neighbor resize propagation, preserved total/folded weights, spacer and
narrow viewport behavior tested against reference fractions. Grid rounding caused
32-row overflow; stack UseLayoutRounding=false fixes it, rotated folded label
rounding remains enabled to retain centering. UiChecks folded geometry now uses
actual region and reference separator-adjusted fraction, retains focus assertion.
Full Release/Git checks PASS auxiliary-checks.log; format PASS auxiliary-format.log.
No publish this turn: canonical PID52834 still runs previous collision package.
Goal active; native auxiliary inspection/publish and remaining COMPLETION gates
pending. No benchmarks, agents, commits or pushes.

2026-09-28: Continued goal, previous turn progress. Auxiliary native PID57786
session1975 capture110599 auxiliary-native.png inspected fractional folded stack.
Auxiliary publish/signature/published R2R PASS; canonical58177/session40264 later
exited, confirmed sessionexit0 and pgrepabsent before nextpublish.
User steering: Find tab dropdown (notdialog), conditional magnifier on overflow,
Markdown links baseline, Specs spaced em/en dash compaction and hover role tags,
move application state ~/.sharprail; search placeholder misposition/focusframe.
Implemented TabSearch.axaml/.cs fifth compiledXAML; Flyout bottom-end offset6
width288, filterbytitle/path, firstresult, arrowwrap, Enter/singleclick, empty,
Escape restoredtriggerfocus. Presenter style in App.axaml retains defaulttemplate;
no replacementtheme. TextBox focus borderremoved; placeholder muted, selectedrow
Ui.Hover, no green selection. Show only clippedstrips, hidepopupifnooverflow.
MarkdownLink measures actual labelbaseline via TextBlock.SetBaselineOffset;
MinHeight/Width0, emphasis preserved. Rendered tests14/24 pass baseline<1px.
Specs compact Regex whitespace+[em/en]+whitespace to middledot, roles source
mapping including Main spec, role hover/focus, accessiblehelp includes role.
Tests realpointerhover/typehidden/titlefixturecompact pass. Navigation checks
assert no ownedwindow/width288, pathfilter, keyboardselection, empty/Escape.
CtrlW remains focusedtab command as reference; test refocuses tab afterEscape.
Default ProfileStore home .sharprail, legacyproject profile migration whenhome
absent; SHARPRAIL_PROFILE explicitoverride retained. Physically moved repo
.sharprail to the home profile directory (destination absent), copied latest
published-stable-tabs/profile.json to home/profile.json. Existing home profile
last project (a local clone of JetBrains/thinkrail:main) preserved, not reset.
FullRelease/Git PASS review-fixes-checks, format PASS review-fixes-format,
publishPASS review-fixes-publish, strict/deepcodesignPASS, publishedR2Rfullchecks
PASS review-fixes-published-checks. Canonical PID63236/session28223 launched
withoutprofileoverride, mounted, ownwindow110986 captured inspected
review-fixes-published-window.png. Search absent fittingstrips, specscompact,
workspace restored. Native search drivers61478/61643/62770 all exited perlive
sessions/pgrep; finalpopupcapture notverified (earlierrootcaptureexcludedpopup).
Do not claim finalpopupnative perfection or broadgoalcomplete. GoalACTIVE.
Remaining: nativepopup/Markdown visualcapture, combineddock/menu contract audit,
other typography/control fidelity gates in COMPLETION. No benches/agents/commits.

2026-09-28 correction: authoritative upstream is [JetBrains/thinkrail:main](https://github.com/JetBrains/thinkrail/tree/main). User interrupted ongoing selection/dropdown fixes to
correct reference and asked whether upstream E2E scenarios can be mirrored.
Inspected upstream e2e/layout.spec.ts test names, preview-tabs.spec.ts cases,
markdown-links.spec.ts actual scenarios, fixtures/app.ts imports (Playwright/
browser wire and Pi fixtures cannot be reused unchanged). Native Avalonia
scenario equivalents possible via existing Headless checks plus native macOS
E2E layer; no instruction yet to implement a complete port.
Compared sources: SpecsPanel/specTree/generated colors/dark theme/command
identical; Workbench.tsx and layout/model.ts DIFFER. Must re-audit docking
against corrected upstream; previous evidence not enough for that contract.
Current unpublised edits: TabSearch selected template ContentPresenter background
fix; placeholder resource TextControlPlaceholderOpacity=1 (old style setter
couldn't override template local opacity.5). New NavigationChecks verifies actual
selected presenter color and actual placeholder opacity/foreground. Native popup
own PID63868 window111051 dropdown-native-popup.png revealed green selection;
fixed capture PID64008 window111090 dropdown-style-native-popup.png verified
muted selection but dim placeholder before resource fix. Helpers new
window-own-all-layers.m filtered ownPID captures popup layers (root screenshots
exclude popup). Both review drivers may still be live: inspect before assumptions.
Selection user correction: use corrected upstream theme selection #6ac8ff26,
Markdown40%alpha=>15; light #6b57ff38 =>previewalpha22. Shared Ui.TextSelection/
PreviewSelection mutate onthemechange, App.axaml default TextBox/Selectable styles
with null selectionforeground; MarkdownPreview assigns PreviewSelection to all
selectable descendants. Fullselection-checks PASS (session27509), changes NOT
published yet, no native selection capture/regression added yet. Canonical63236
session28223 still previousreviewfixespkg, revalidate before overwrite. GoalACTIVE.

2026-09-28 authorized upstream E2E translation is now IN SCOPE. Source remains
[JetBrains/thinkrail:main](https://github.com/JetBrains/thinkrail/tree/main). Added E2E.md with per-case
inventory for preview/Markdown/layout/projects/workspaces/theme/width/settings/
changes/chrome, honest Pending statuses and explicit editor/terminal/AI exclusions.
Added tests/SharpRail.Checks/E2E/{E2eWorkspace,PreviewTabsE2E,MarkdownLinksE2E,
LayoutE2E}.cs. Eleven translated cases PASS: all8 preview, both Markdown links,
layout independent preview slots. Driver uses real pointer presses, dispatcher
MainLoop bounded10ms, render ticks, hit-test guard, resolves rebuilt named/file
controls, moves pointer off tooltips. Replaced invalid upstream1pxPNG fixture
(IDAT badCRC/truncated zlib; Skia rejects) with valid1pxPNG; documented adaptation.
New SelectionChecks verifies actual TextBox/Markdown selection brushes and null
foreground in dark/light themes; PASS. UiChecks invokes translated cases.
Fixed actual bugs: active preview click schedules250ms keep with nav/epochguard;
DockTabButton uses Button style key; file/spec clicks share one in-flight read,
double click upgrades pending preview tokeep and claims preview slot; stamped at
click time, abandoned browse discarded, overtaken keep retained without stealing
selection. LayoutSession.Open optional claimPreview/activate supports these;
programmatic OpenDocumentAsync retained old stale-request discard contract.
Markdown relativefilelink now openspreview notkepttab. Removed obsoletefileClick.
Full Release checks PASS with Git source correctedupstream; format verify PASS.
Published scripts/publish.sh PASS, codesignstrictdeepPASS; publishedR2Rfullchecks
session69265 still running at record time (.bench/upstream-e2e-published-checks.log),
all11 translated cases plus selection alreadyPASS. Finalrestofsuiteawaitcompletion.
Canonical app PID85220/session53829 now running updatedpackage, noenvprofile
override. Previous63236 andreviewdriverswereabsentpgrepbeforeoverwrite. Ownroot
window111492 captured .bench/upstream-e2e-native-window.png and inspected. Empty
restoredworkspace, Specs/Review panes, appvisuallymounted. Leave running.
No benchmarks/agents/commits/pushes. GoalACTIVE, fullupstreamtranslationNOTcomplete.
NEXT: finishpublishedchecks; continue pending E2E suites from actual upstream
steps, fix concrete failures. Native finalpopup/selection visualcapture still
pending; corrected source docking audit and full screenshot parity remain gates.
