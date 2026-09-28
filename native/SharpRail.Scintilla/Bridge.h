#pragma once
#include "Includes.h"
using namespace Scintilla;
using namespace Scintilla::Internal;

// Fixed C ABI. Text and point buffers are borrowed only for the callback duration.
struct DrawCommand {
    int operation = 0;
    int surface = 0;
    double left = 0, top = 0, right = 0, bottom = 0;
    double size = 0, width = 0, baseline = 0;
    unsigned int fore = 0, back = 0;
    const void *data = nullptr;
    intptr_t length = 0;
    double *positions = nullptr;
    int weight = 400, italic = 0;
};
using DrawCallback = double (*)(void *, const DrawCommand *);
struct BridgeWindow {
    DrawCallback draw;
    void *context;
    PRectangle bounds {0, 0, 800, 600};
};
extern thread_local BridgeWindow *activeWindow;
