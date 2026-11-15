/* Source-only placeholder. Generate game icons into the local SDL build tree. */
#ifndef TP_CONTROL_ICONS_H
#define TP_CONTROL_ICONS_H
#include <stddef.h>
typedef struct { const char *name; const unsigned char *png; size_t len; } TPIconData;
static const TPIconData TPIcons[] = {{"", NULL, 0}};
#endif
