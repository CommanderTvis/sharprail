#include "Bridge.h"
#include <cstdarg>

thread_local BridgeWindow *activeWindow = nullptr;
namespace {
class SkiaFont final : public Font {
public:
    FontSpec spec;
    explicit SkiaFont(const FontParameters &p) : spec{p.size, static_cast<int>(p.weight), p.italic} {}
};
class SkiaSurface final : public Surface {
    BridgeWindow *window = activeWindow;
    bool owns = false;
    double Call(DrawCommand command) { return Dispatch(window, id, command); }
    DrawCommand Rect(int op, PRectangle r, ColourRGBA fore = ColourRGBA(), ColourRGBA back = ColourRGBA(), double width = 0) {
        DrawCommand c;
        c.operation = op; c.left = r.left; c.top = r.top; c.right = r.right; c.bottom = r.bottom;
        c.fore = fore.AsInteger(); c.back = back.AsInteger(); c.width = width;
        return c;
    }
    DrawCommand Text(int op, const Font *font, std::string_view text) {
        const auto f = SpecOf(font);
        DrawCommand c;
        c.operation = op; c.size = f.size; c.weight = f.weight; c.italic = f.italic;
        c.data = text.data(); c.length = text.size();
        return c;
    }
    void Shape(int op, PRectangle r, FillStroke fs, double radius = 0) {
        auto c = Rect(op, r, fs.stroke.colour, fs.fill.colour, fs.stroke.width);
        c.size = radius; Call(c);
    }
public:
    int id = 0;
    ~SkiaSurface() override { Release(); }
    void Init(WindowID wid) override { window = static_cast<BridgeWindow *>(wid); }
    void Init(SurfaceID, WindowID wid) override { Init(wid); }
    std::unique_ptr<Surface> AllocatePixMap(int width, int height) override {
        auto s = std::make_unique<SkiaSurface>(); s->window = window;
        s->id = static_cast<int>(Call(Rect(1, PRectangle(0, 0, width, height))));
        s->owns = true; return s;
    }
    void SetMode(SurfaceMode) override {}
    void Release() noexcept override { if (owns) { Call(Rect(2, PRectangle())); owns = false; } }
    int SupportsFeature(Supports feature) noexcept override { return feature == Supports::FractionalStrokeWidth || feature == Supports::TranslucentStroke || feature == Supports::PixelModification; }
    bool Initialised() override { return window != nullptr; }
    int LogPixelsY() override { return 72; }
    int PixelDivisions() override { return 1; }
    int DeviceHeightFont(int points) override { return points; }
    void LineDraw(Point a, Point b, Stroke s) override { Call(Rect(3, PRectangle(a.x,a.y,b.x,b.y),s.colour,ColourRGBA(),s.width)); }
    void PolyLine(const Point *p, size_t n, Stroke s) override { auto c=Rect(4,PRectangle(),s.colour,ColourRGBA(),s.width); c.data=p;c.length=n;Call(c); }
    void Polygon(const Point *p, size_t n, FillStroke s) override { auto c=Rect(5,PRectangle(),s.stroke.colour,s.fill.colour,s.stroke.width);c.data=p;c.length=n;Call(c); }
    void RectangleDraw(PRectangle r, FillStroke s) override { Shape(6,r,s); }
    void RectangleFrame(PRectangle r, Stroke s) override { Call(Rect(7,r,s.colour,ColourRGBA(),s.width)); }
    void FillRectangle(PRectangle r, Fill f) override { Call(Rect(8,r,f.colour)); }
    void FillRectangleAligned(PRectangle r, Fill f) override { FillRectangle(r,f); }
    void FillRectangle(PRectangle r, Surface &pattern) override { auto c=Rect(9,r);c.length=static_cast<SkiaSurface &>(pattern).id;Call(c); }
    void RoundedRectangle(PRectangle r, FillStroke s) override { Shape(10,r,s,4); }
    void AlphaRectangle(PRectangle r, XYPOSITION corner, FillStroke s) override { Shape(10,r,s,corner); }
    void GradientRectangle(PRectangle r, const std::vector<ColourStop> &stops, GradientOptions options) override {
        // Transfer stops explicitly: native ColourStop padding is not part of the ABI.
        std::vector<double> values; for (const auto &stop : stops) { values.push_back(stop.position);values.push_back(stop.colour.AsInteger()); }
        auto c=Rect(11,r);c.data=values.data();c.length=stops.size();c.width=options==GradientOptions::topToBottom;Call(c);
    }
    void DrawRGBAImage(PRectangle r,int width,int height,const unsigned char *pixels) override { auto c=Rect(12,r);c.data=pixels;c.width=width;c.size=height;Call(c); }
    void Ellipse(PRectangle r,FillStroke s) override { Shape(13,r,s); }
    void Stadium(PRectangle r,FillStroke s,Ends) override { Shape(10,r,s,r.Height()/2); }
    void Copy(PRectangle r,Point from,Surface &source) override { auto c=Rect(14,r);c.length=static_cast<SkiaSurface &>(source).id;c.width=from.x;c.size=from.y;Call(c); }
    std::unique_ptr<IScreenLineLayout> Layout(const IScreenLine *line) override { return LayoutScreenLine(window, line); }
    void DrawTextNoClip(PRectangle r,const Font *f,XYPOSITION y,std::string_view t,ColourRGBA fore,ColourRGBA back) override { FillRectangle(r,back);DrawTextTransparent(r,f,y,t,fore); }
    void DrawTextClipped(PRectangle r,const Font *f,XYPOSITION y,std::string_view t,ColourRGBA fore,ColourRGBA back) override { SetClip(r);DrawTextNoClip(r,f,y,t,fore,back);PopClip(); }
    void DrawTextTransparent(PRectangle r,const Font *f,XYPOSITION y,std::string_view t,ColourRGBA fore) override { auto c=Text(15,f,t);c.left=r.left;c.baseline=y;c.fore=fore.AsInteger();Call(c); }
    void MeasureWidths(const Font *f,std::string_view t,XYPOSITION *positions) override { auto c=Text(16,f,t);c.positions=positions;Call(c); }
    XYPOSITION WidthText(const Font *f,std::string_view t) override { return Call(Text(17,f,t)); }
    void DrawTextNoClipUTF8(PRectangle r,const Font *f,XYPOSITION y,std::string_view t,ColourRGBA fore,ColourRGBA back) override { DrawTextNoClip(r,f,y,t,fore,back); }
    void DrawTextClippedUTF8(PRectangle r,const Font *f,XYPOSITION y,std::string_view t,ColourRGBA fore,ColourRGBA back) override { DrawTextClipped(r,f,y,t,fore,back); }
    void DrawTextTransparentUTF8(PRectangle r,const Font *f,XYPOSITION y,std::string_view t,ColourRGBA fore) override { DrawTextTransparent(r,f,y,t,fore); }
    void MeasureWidthsUTF8(const Font *f,std::string_view t,XYPOSITION *positions) override { MeasureWidths(f,t,positions); }
    XYPOSITION WidthTextUTF8(const Font *f,std::string_view t) override { return WidthText(f,t); }
    XYPOSITION Ascent(const Font *f) override { return Call(Text(18,f,{})); }
    XYPOSITION Descent(const Font *f) override { return Call(Text(19,f,{})); }
    XYPOSITION InternalLeading(const Font *) override { return 0; }
    XYPOSITION Height(const Font *f) override { return Call(Text(20,f,{})); }
    XYPOSITION AverageCharWidth(const Font *f) override { return WidthText(f,"n"); }
    void SetClip(PRectangle r) override { Call(Rect(21,r)); }
    void PopClip() override { Call(Rect(22,PRectangle())); }
    void FlushCachedState() override {}
    void FlushDrawing() override {}
};
}
FontSpec SpecOf(const Font *font) noexcept { return static_cast<const SkiaFont *>(font)->spec; }
int SurfaceId(Surface &surface) noexcept { return static_cast<SkiaSurface &>(surface).id; }
double Dispatch(BridgeWindow *window, int surface, DrawCommand command) {
    command.surface = surface;
    return window->draw(window->context, &command);
}
std::shared_ptr<Font> Font::Allocate(const FontParameters &p) { return std::make_shared<SkiaFont>(p); }
std::unique_ptr<Surface> Surface::Allocate(Technology) { return std::make_unique<SkiaSurface>(); }
Window::~Window() noexcept = default;
void Window::Destroy() noexcept { wid=nullptr; }
PRectangle Window::GetPosition() const { return wid ? static_cast<BridgeWindow *>(wid)->bounds : PRectangle(); }
void Window::SetPosition(PRectangle r) { static_cast<BridgeWindow *>(wid)->bounds=r; }
void Window::SetPositionRelative(PRectangle r,const Window *) { SetPosition(r); }
PRectangle Window::GetClientPosition() const { return GetPosition(); }
void Window::Show(bool) {}
void Window::InvalidateAll() { if (wid) static_cast<BridgeWindow *>(wid)->dirty=true; }
void Window::InvalidateRectangle(PRectangle) { InvalidateAll(); }
void Window::SetCursor(Cursor c) { cursorLast=c; }
PRectangle Window::GetMonitorRect(Point) { return GetPosition(); }
ColourRGBA Platform::Chrome() { return ColourRGBA(40,40,40); }
ColourRGBA Platform::ChromeHighlight() { return ColourRGBA(70,70,70); }
const char *Platform::DefaultFont() { return "JetBrains Mono"; }
int Platform::DefaultFontSize() { return 13; }
unsigned int Platform::DoubleClickTime() { return 500; }
void Platform::DebugDisplay(const char *s) noexcept { std::fputs(s,stderr); }
void Platform::DebugPrintf(const char *format,...) noexcept { va_list args;va_start(args,format);std::vfprintf(stderr,format,args);va_end(args); }
bool Platform::ShowAssertionPopUps(bool) noexcept { return false; }
void Platform::Assert(const char *c,const char *file,int line) noexcept { std::fprintf(stderr,"Scintilla assertion: %s (%s:%d)\n",c,file,line);std::abort(); }
