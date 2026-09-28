#include "Bridge.h"

class SkiaEditor final : public Editor {
    bool captured = false;
public:
    BridgeWindow window;
    intptr_t revision = 0;
    SkiaEditor(DrawCallback callback,void *context) : window{callback,context} {
        wMain=&window; activeWindow=&window;
        Initialise();
    }
    ~SkiaEditor() override { Finalise(); }
    void Initialise() override {
        WndProc(Message::SetCodePage,65001,0);
        WndProc(Message::SetBufferedDraw,0,0);
        WndProc(Message::SetWrapMode,static_cast<uptr_t>(Wrap::None),0);
    }
    void SetHorizontalScrollPos() override {}
    bool ModifyScrollBars(Sci::Line,Sci::Line) override { return false; }
    void Copy() override {}
    void Paste() override {}
    void ClaimSelection() override {}
    void NotifyChange() override { ++revision; }
    void NotifyParent(NotificationData) override {}
    void CopyToClipboard(const SelectionText &) override {}
    void SetMouseCapture(bool on) override { captured=on; }
    bool HaveMouseCapture() override { return captured; }
    std::string UTF8FromEncoded(std::string_view s) const override { return std::string(s); }
    std::string EncodedFromUTF8(std::string_view s) const override { return std::string(s); }
    sptr_t DefWndProc(Message,uptr_t,sptr_t) override { return 0; }
    void Resize(double width,double height) { window.bounds=PRectangle(0,0,width,height);ChangeSize(); }
    void Draw() {
        AutoSurface surface(this);
        paintState=PaintState::painting;rcPaint=window.bounds;paintingAllText=true;
        Paint(surface,window.bounds);
        paintState=PaintState::notPainting;
    }
    void Input(std::string_view text) { InsertCharacter(text,CharacterSource::DirectInput); }
    bool Key(int key,int modifiers) { bool consumed=false;KeyDownWithModifiers(static_cast<Keys>(key),static_cast<KeyMod>(modifiers),&consumed);return consumed; }
    void Mouse(int kind,double x,double y,unsigned int time,int modifiers) {
        auto m=static_cast<KeyMod>(modifiers);
        if (kind==0) ButtonDownWithModifiers(Point(x,y),time,m);
        else if (kind==1) ButtonMoveWithModifiers(Point(x,y),time,m);
        else ButtonUpWithModifiers(Point(x,y),time,m);
    }
    void Focus(bool focus) { SetFocusState(focus); }
    void Tick() { TickFor(TickReason::caret);if(captured) TickFor(TickReason::scroll); }
};
// Native exceptions never cross the managed boundary. A separate error flag lets
// callers distinguish a legitimate zero result from a failed operation.
static thread_local bool failed=false;
template<typename F> intptr_t Guard(SkiaEditor *e,F f) noexcept {
    failed=false;activeWindow=e ? &e->window : nullptr;
    try { return f(); } catch (...) { failed=true;return 0; }
}
#define API extern "C" __attribute__((visibility("default")))
API void *sr_create(DrawCallback callback,void *context) noexcept { return reinterpret_cast<void *>(Guard(nullptr,[&] { return reinterpret_cast<intptr_t>(new SkiaEditor(callback,context)); })); }
API int sr_failed() noexcept { return failed; }
API void sr_destroy(SkiaEditor *e) noexcept { Guard(e,[&] { delete e;return 0; });activeWindow=nullptr; }
API intptr_t sr_send(SkiaEditor *e,unsigned int message,uintptr_t w,intptr_t l) noexcept { return Guard(e,[&] { return e->WndProc(static_cast<Message>(message),w,l); }); }
API void sr_resize(SkiaEditor *e,double w,double h) noexcept { Guard(e,[&] { e->Resize(w,h);return 0; }); }
API void sr_paint(SkiaEditor *e) noexcept { Guard(e,[&] { e->Draw();return 0; }); }
API void sr_text(SkiaEditor *e,const char *text,int length) noexcept { Guard(e,[&] { e->Input({text,static_cast<size_t>(length)});return 0; }); }
API int sr_key(SkiaEditor *e,int key,int modifiers) noexcept { return static_cast<int>(Guard(e,[&] { return e->Key(key,modifiers); })); }
API void sr_mouse(SkiaEditor *e,int kind,double x,double y,unsigned int time,int modifiers) noexcept { Guard(e,[&] { e->Mouse(kind,x,y,time,modifiers);return 0; }); }
API void sr_focus(SkiaEditor *e,int focus) noexcept { Guard(e,[&] { e->Focus(focus);return 0; }); }
API void sr_tick(SkiaEditor *e) noexcept { Guard(e,[&] { e->Tick();return 0; }); }

API intptr_t sr_revision(SkiaEditor *e) noexcept { return e->revision; }
