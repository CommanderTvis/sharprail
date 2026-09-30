#pragma once
#include "Includes.h"
#include <list>
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
    intptr_t start = 0, end = 0;
    int rtl = 0;
};
using DrawCallback = double (*)(void *, const DrawCommand *);

struct FontSpec {
    double size = 0;
    int weight = 400;
    bool italic = false;
    bool operator==(const FontSpec &other) const noexcept { return size == other.size && weight == other.weight && italic == other.italic; }
};
FontSpec SpecOf(const Font *font) noexcept;

enum class Direction { LeftToRight, RightToLeft, Auto };

// A screen line after bidi reordering and shaping. Clusters are caret stops and runs are
// shaped text; both are in visual order and use byte offsets into the line.
struct LineShape {
    struct Item {
        size_t start, end;
        FontSpec font;
        double width; // A representation's fixed width; 0 for text, -1 for a tab.
        bool operator==(const Item &other) const noexcept { return start == other.start && end == other.end && font == other.font && width == other.width; }
    };
    struct Cluster { size_t start, end; double left, width; bool rtl; };
    struct Run { size_t start, end; FontSpec font; double left; bool rtl; };
    std::string text;
    std::vector<Item> items;
    Direction direction = Direction::LeftToRight;
    double tabWidth = 0, tabMinimum = 0;
    size_t hash = 0;
    std::vector<Cluster> clusters;
    std::vector<Run> runs;
};

struct BridgeWindow {
    DrawCallback draw;
    void *context;
    PRectangle bounds {0, 0, 800, 600};
    Direction direction = Direction::LeftToRight;
    std::list<std::shared_ptr<const LineShape>> shapes; // Most recently used first.
};
extern thread_local BridgeWindow *activeWindow;

double Dispatch(BridgeWindow *window, int surface, DrawCommand command);
int SurfaceId(Surface &surface) noexcept;
std::unique_ptr<IScreenLineLayout> LayoutScreenLine(BridgeWindow *window, const IScreenLine *line);
