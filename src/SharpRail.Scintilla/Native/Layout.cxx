// Bidirectional line layout: SheenBidi orders runs, the managed renderer shapes them.
#include "Bridge.h"
#include <SheenBidi/SheenBidi.h>

namespace {
constexpr int ShapeGraphemes = 23, DrawShaped = 24;
constexpr size_t CachedShapes = 256;

using Item = LineShape::Item;
struct Level { size_t start, end; bool rtl; };
struct Grapheme { size_t start, end; double width; };

// Splits the line where the font changes and around tabs and representations.
std::vector<Item> Items(const IScreenLine *line, std::string_view text) {
    std::vector<Item> items;
    for (size_t position = 0; position < text.size();) {
        const size_t next = position + UTF8DrawBytes(text.data() + position, text.size() - position);
        const FontSpec font = SpecOf(line->FontOfPosition(position));
        const double representation = line->RepresentationWidth(position);
        const double width = representation > 0 ? representation : text[position] == '\t' ? -1 : 0;
        if (width == 0 && !items.empty() && items.back().width == 0 && items.back().font == font)
            items.back().end = next;
        else
            items.push_back({position, next, font, width});
        position = next;
    }
    return items;
}

// Runs of one embedding level in visual order. Line ends stay last, in the paragraph direction.
std::vector<Level> VisualRuns(std::string_view text, Direction direction) {
    const size_t body = std::min(text.find_first_of("\r\n"), text.size());
    bool rtl = direction == Direction::RightToLeft;
    std::vector<Level> runs;
    // Only RTL characters or an RTL base can reorder; ASCII has neither.
    const bool ascii = std::all_of(text.begin(), text.begin() + body, [](char c) { return static_cast<unsigned char>(c) < 0x80; });
    if (ascii && !rtl) {
        if (body > 0) runs.push_back({0, body, false});
    } else {
        const SBCodepointSequence sequence {SBStringEncodingUTF8, const_cast<char *>(text.data()), body};
        const SBAlgorithmRef algorithm = SBAlgorithmCreate(&sequence);
        const SBLevel base = direction == Direction::LeftToRight ? 0 : direction == Direction::RightToLeft ? 1 : SBLevelDefaultLTR;
        // Paragraph separators such as U+2029 split the line into consecutive paragraphs.
        for (size_t offset = 0; offset < body;) {
            const SBParagraphRef paragraph = SBAlgorithmCreateParagraph(algorithm, offset, body - offset, base);
            if (offset == 0) rtl = (SBParagraphGetBaseLevel(paragraph) & 1) != 0;
            const SBLineRef line = SBParagraphCreateLine(paragraph, offset, SBParagraphGetLength(paragraph));
            const SBRun *levels = SBLineGetRunsPtr(line);
            for (SBUInteger i = 0; i < SBLineGetRunCount(line); i++)
                runs.push_back({levels[i].offset, levels[i].offset + levels[i].length, (levels[i].level & 1) != 0});
            offset += SBParagraphGetLength(paragraph);
            SBLineRelease(line);
            SBParagraphRelease(paragraph);
        }
        SBAlgorithmRelease(algorithm);
    }
    if (body < text.size()) runs.push_back({body, text.size(), rtl});
    return runs;
}

// Shapes text in one direction; appends its graphemes in logical order and returns the total advance.
double Shape(BridgeWindow *window, std::string_view text, size_t offset, const FontSpec &font, bool rtl, std::vector<Grapheme> &graphemes) {
    std::vector<double> ends(text.size());
    DrawCommand command;
    command.operation = ShapeGraphemes; command.size = font.size; command.weight = font.weight; command.italic = font.italic;
    command.data = text.data(); command.length = static_cast<intptr_t>(text.size()); command.positions = ends.data(); command.rtl = rtl;
    const double width = Dispatch(window, 0, command);
    // The renderer marks each grapheme's last byte with the advance up to its end and other bytes with -1.
    double previous = 0;
    size_t start = 0;
    for (size_t byte = 0; byte < text.size(); byte++) {
        if (ends[byte] < 0) continue;
        graphemes.push_back({offset + start, offset + byte + 1, ends[byte] - previous});
        previous = ends[byte]; start = byte + 1;
    }
    if (start < text.size()) {
        if (graphemes.empty()) graphemes.push_back({offset, offset + text.size(), width});
        else graphemes.back().end = offset + text.size();
    }
    return width;
}

std::shared_ptr<const LineShape> Build(BridgeWindow *window, const IScreenLine *line, LineShape shape) {
    const std::string_view text = shape.text;
    std::vector<Grapheme> graphemes;
    double x = 0;
    for (const Level &level : VisualRuns(text, shape.direction)) {
        std::vector<Item> pieces;
        for (const Item &item : shape.items) {
            const size_t start = std::max(item.start, level.start), end = std::min(item.end, level.end);
            if (start < end) pieces.push_back({start, end, item.font, item.width});
        }
        if (level.rtl) std::reverse(pieces.begin(), pieces.end());
        for (const Item &piece : pieces) {
            if (piece.width != 0) {
                const double width = piece.width > 0 ? piece.width : line->TabPositionAfter(x) - x;
                shape.clusters.push_back({piece.start, piece.end, x, width, level.rtl});
                x += width;
                continue;
            }
            graphemes.clear();
            const double width = Shape(window, text.substr(piece.start, piece.end - piece.start), piece.start, piece.font, level.rtl, graphemes);
            shape.runs.push_back({piece.start, piece.end, piece.font, x, level.rtl});
            // Right-to-left graphemes are placed from the last logical one leftwards.
            if (level.rtl) std::reverse(graphemes.begin(), graphemes.end());
            for (const Grapheme &grapheme : graphemes) {
                shape.clusters.push_back({grapheme.start, grapheme.end, x, grapheme.width, level.rtl});
                x += grapheme.width;
            }
            x = shape.runs.back().left + width;
        }
    }
    return std::make_shared<const LineShape>(std::move(shape));
}

class ShapedLayout final : public IScreenLineLayout {
    BridgeWindow *window;
    std::shared_ptr<const LineShape> shape;
public:
    ShapedLayout(BridgeWindow *window, std::shared_ptr<const LineShape> shape) : window(window), shape(std::move(shape)) {}

    size_t PositionFromX(XYPOSITION x, bool charPosition) override {
        const auto &clusters = shape->clusters;
        if (clusters.empty()) return 0;
        const auto hit = std::find_if(clusters.begin(), clusters.end(), [x](const auto &c) { return x < c.left + c.width; });
        const auto &cluster = hit == clusters.end() ? clusters.back() : *hit;
        if (charPosition) return cluster.start;
        // The half nearer the cluster's logical start maps to its start.
        const bool left = x < cluster.left + cluster.width / 2;
        return left != cluster.rtl ? cluster.start : cluster.end;
    }

    // A caret sits on the leading edge of the cluster it precedes, or else on the trailing
    // edge of the cluster it follows, as at the end of the line.
    XYPOSITION XFromPosition(size_t position) override {
        const auto &clusters = shape->clusters;
        for (const auto &c : clusters)
            if (c.start == position) return c.rtl ? c.left + c.width : c.left;
        for (const auto &c : clusters)
            if (c.end == position) return c.rtl ? c.left : c.left + c.width;
        for (const auto &c : clusters)
            if (c.start < position && position < c.end) return c.rtl ? c.left + c.width : c.left;
        return clusters.empty() ? 0 : clusters.back().left + clusters.back().width;
    }

    std::vector<Interval> FindRangeIntervals(size_t start, size_t end) override {
        std::vector<Interval> intervals;
        for (const auto &c : shape->clusters) {
            if (c.start >= end || c.end <= start || c.width <= 0) continue;
            if (!intervals.empty() && std::abs(intervals.back().right - c.left) < 0.01)
                intervals.back().right = c.left + c.width;
            else
                intervals.push_back({c.left, c.left + c.width});
        }
        return intervals;
    }

    void DrawText(Surface *surface, size_t start, size_t end, XYPOSITION xStart, XYPOSITION ybase, ColourRGBA fore) override {
        for (const auto &run : shape->runs) {
            if (run.start >= end || run.end <= start) continue;
            DrawCommand command;
            command.operation = DrawShaped; command.size = run.font.size; command.weight = run.font.weight; command.italic = run.font.italic;
            command.data = shape->text.data() + run.start; command.length = static_cast<intptr_t>(run.end - run.start);
            command.left = xStart + run.left; command.baseline = ybase; command.fore = fore.AsInteger(); command.rtl = run.rtl;
            command.start = static_cast<intptr_t>(std::max(start, run.start) - run.start);
            command.end = static_cast<intptr_t>(std::min(end, run.end) - run.start);
            Dispatch(window, SurfaceId(*surface), command);
        }
    }
};
}

std::unique_ptr<IScreenLineLayout> LayoutScreenLine(BridgeWindow *window, const IScreenLine *line) {
    LineShape shape;
    shape.text = std::string(line->Text());
    shape.items = Items(line, shape.text);
    shape.direction = window->direction;
    shape.tabWidth = line->TabWidth();
    shape.tabMinimum = line->TabWidthMinimumPixels();
    shape.hash = std::hash<std::string>{}(shape.text);
    // Scintilla asks for the same line several times per paint: background, text, selection, caret.
    auto &cache = window->shapes;
    for (auto it = cache.begin(); it != cache.end(); ++it) {
        const LineShape &cached = **it;
        if (cached.hash == shape.hash && cached.direction == shape.direction && cached.tabWidth == shape.tabWidth &&
            cached.tabMinimum == shape.tabMinimum && cached.text == shape.text && cached.items == shape.items) {
            cache.splice(cache.begin(), cache, it);
            return std::make_unique<ShapedLayout>(window, cache.front());
        }
    }
    cache.push_front(Build(window, line, std::move(shape)));
    if (cache.size() > CachedShapes) cache.pop_back();
    return std::make_unique<ShapedLayout>(window, cache.front());
}
