/* Terraria port UI: touch input uses SDL's normal keyboard/mouse path.
 * Native controller input remains handled by SDL MFi/GameController + FNA. */
#import <GameController/GameController.h>
#import <AVFoundation/AVFoundation.h>
#include <dlfcn.h>
#import <objc/runtime.h>
#import <objc/message.h>
void SDL_UIKit_Trace(const char *what);
#include "../../events/SDL_keyboard_c.h"
#include "../../events/SDL_mouse_c.h"
#include "../../events/SDL_windowevents_c.h"

/* v28: log SDL window/app lifecycle events and remember the last focus event delivered to the game. */
static int TPLastFocus = 0; /* 1 = FOCUS_GAINED, -1 = FOCUS_LOST, 0 = none yet */
static const char *TPWindowEventName(Uint8 e) {
    switch (e) {
    case SDL_WINDOWEVENT_SHOWN: return "SHOWN"; case SDL_WINDOWEVENT_HIDDEN: return "HIDDEN";
    case SDL_WINDOWEVENT_RESIZED: return "RESIZED"; case SDL_WINDOWEVENT_SIZE_CHANGED: return "SIZE_CHANGED";
    case SDL_WINDOWEVENT_MINIMIZED: return "MINIMIZED"; case SDL_WINDOWEVENT_MAXIMIZED: return "MAXIMIZED"; case SDL_WINDOWEVENT_RESTORED: return "RESTORED";
    case SDL_WINDOWEVENT_FOCUS_GAINED: return "FOCUS_GAINED"; case SDL_WINDOWEVENT_FOCUS_LOST: return "FOCUS_LOST";
    case SDL_WINDOWEVENT_ENTER: return "ENTER"; case SDL_WINDOWEVENT_LEAVE: return "LEAVE";
    case SDL_WINDOWEVENT_DISPLAY_CHANGED: return "DISPLAY_CHANGED"; default: return NULL; }
}
static int SDLCALL TPEventWatch(void *userdata, SDL_Event *e) {
    (void) userdata;
    if (e->type == SDL_WINDOWEVENT) {
        const char *n = TPWindowEventName(e->window.event);
        if (e->window.event == SDL_WINDOWEVENT_FOCUS_GAINED) TPLastFocus = 1;
        else if (e->window.event == SDL_WINDOWEVENT_FOCUS_LOST) TPLastFocus = -1;
        if (n) SDL_UIKit_Trace([[NSString stringWithFormat:@"sdl window event %s data=%d,%d", n, e->window.data1, e->window.data2] UTF8String]);
    } else if (e->type >= SDL_APP_TERMINATING && e->type <= SDL_APP_DIDENTERFOREGROUND) {
        static const char *names[] = {"APP_TERMINATING","APP_LOWMEMORY","APP_WILLENTERBACKGROUND","APP_DIDENTERBACKGROUND","APP_WILLENTERFOREGROUND","APP_DIDENTERFOREGROUND"};
        SDL_UIKit_Trace([[NSString stringWithFormat:@"sdl app event %s", names[e->type - SDL_APP_TERMINATING]] UTF8String]);
    }
    return 1;
}

#include <mach/mach.h>
#include <mach/task_info.h>
static BOOL TPEditing = NO; /* layout editor active: controls do not send input */
static double TPAppFootprintGB(void) {
    task_vm_info_data_t info; mach_msg_type_number_t n = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t) &info, &n) != KERN_SUCCESS) return -1;
    return info.phys_footprint / 1073741824.0;
}
/* Memory breakdown for diagnosis: total footprint, graphics (GPU textures/buffers), compressed, and the rest. */
static NSString *TPMemoryBreakdown(void) {
    task_vm_info_data_t i; mach_msg_type_number_t n = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t) &i, &n) != KERN_SUCCESS) return @"memory: task_info failed";
    double G = 1073741824.0;
    return [NSString stringWithFormat:@"memory footprint=%.2f GB graphics=%.2f GB internal=%.2f GB compressed=%.2f GB purgeable=%.2f GB limit-rem=%.2f GB",
        i.phys_footprint/G, i.ledger_tag_graphics_footprint/G, i.internal/G, i.compressed/G, (i.ledger_purgeable_nonvolatile+i.ledger_purgeable_volatile)/G, i.limit_bytes_remaining/G];
}
static double TPGraphicsGB(void) {
    task_vm_info_data_t i; mach_msg_type_number_t n = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t) &i, &n) != KERN_SUCCESS) return -1;
    return i.ledger_tag_graphics_footprint / 1073741824.0;
}
static double TPCPUPercent(void) {
    thread_act_array_t threads; mach_msg_type_number_t count = 0; double total = 0;
    if (task_threads(mach_task_self(), &threads, &count) != KERN_SUCCESS) return -1;
    for (mach_msg_type_number_t i = 0; i < count; i++) {
        thread_basic_info_data_t b; mach_msg_type_number_t c = THREAD_BASIC_INFO_COUNT;
        if (thread_info(threads[i], THREAD_BASIC_INFO, (thread_info_t) &b, &c) == KERN_SUCCESS && !(b.flags & TH_FLAGS_IDLE))
            total += b.cpu_usage / (double) TH_USAGE_SCALE * 100.0;
        mach_port_deallocate(mach_task_self(), threads[i]);
    }
    vm_deallocate(mach_task_self(), (vm_address_t) threads, count * sizeof(thread_t));
    return total;
}
static const char *TPThermalName(void) {
    switch (NSProcessInfo.processInfo.thermalState) {
    case NSProcessInfoThermalStateNominal: return "Nominal"; case NSProcessInfoThermalStateFair: return "Fair";
    case NSProcessInfoThermalStateSerious: return "Serious"; case NSProcessInfoThermalStateCritical: return "Critical"; default: return "?"; }
}
#include "SDL_terraria_icons.h"
/* v34: controls styled after official iOS Terraria (navy discs with dark rim, square inventory-slot tiles, Terraria item sprites). */
static UIImage *TPIcon(NSString *name) {
    static NSMutableDictionary *cache; if(!cache)cache=[NSMutableDictionary new];
    UIImage *i=cache[name]; if(i)return i;
    for(size_t k=0;k<sizeof(TPIcons)/sizeof(TPIcons[0]);k++) if(!strcmp(TPIcons[k].name,name.UTF8String)){
        i=[UIImage imageWithData:[NSData dataWithBytesNoCopy:(void*)TPIcons[k].png length:TPIcons[k].len freeWhenDone:NO]]; if(i)cache[name]=i; return i; }
    return nil;
}
static int TPZoomSteps = 0; /* consumed by managed code (TP_TakeZoomSteps) */
DECLSPEC int SDLCALL TP_TakeZoomSteps(void) { int n=TPZoomSteps; TPZoomSteps=0; return n; }
static BOOL TPAimUse(void) { NSUserDefaults *d=NSUserDefaults.standardUserDefaults; return [d objectForKey:@"TPAimUse"]?[d boolForKey:@"TPAimUse"]:YES; }
typedef NS_ENUM(int, TPKind) { TPKindHold=0, TPKindTap=1, TPKindAimToggle=2, TPKindPanel=3, TPKindZoomIn=4, TPKindZoomOut=5 };
static UIColor *TPNavy(void){ return [UIColor colorWithRed:0.22 green:0.29 blue:0.60 alpha:1]; }
static UIColor *TPRim(void){ return [UIColor colorWithRed:0.07 green:0.08 blue:0.18 alpha:1]; }
/* disc: dark outer rim, coloured body, lighter top highlight band like the official pixel discs */
static void TPDrawDisc(CGContextRef c, CGRect r, UIColor *body, BOOL pressed) {
    CGFloat rim=MAX(2,r.size.width*0.045);
    CGContextSetFillColorWithColor(c,TPRim().CGColor);CGContextFillEllipseInRect(c,r);
    CGRect in=CGRectInset(r,rim,rim); CGFloat h,s,b,a; [body getHue:&h saturation:&s brightness:&b alpha:&a];
    UIColor *base=pressed?[UIColor colorWithHue:h saturation:s brightness:b*0.75 alpha:a]:body;
    CGContextSetFillColorWithColor(c,[UIColor colorWithHue:h saturation:s*0.9 brightness:MIN(1,b*1.25) alpha:a].CGColor);CGContextFillEllipseInRect(c,in);
    CGContextSetFillColorWithColor(c,base.CGColor);CGContextFillEllipseInRect(c,CGRectMake(in.origin.x,in.origin.y+in.size.height*0.06,in.size.width,in.size.height*0.94));
}
@interface TPActionButton : UIButton
@property(nonatomic,assign) SDL_Window *sdl;
@property(nonatomic,assign) SDL_Scancode scan;
@property(nonatomic,assign) int mouseButton;
@property(nonatomic,assign) BOOL inputHeld;
@property(nonatomic,assign) TPKind kind;
@property(nonatomic,assign) BOOL square;
@property(nonatomic,strong) UIColor *body;
@property(nonatomic,copy) NSString *glyph;   /* SF Symbol drawn when there is no sprite */
@property(nonatomic,strong) UIImage *sprite; /* Terraria item sprite, drawn pixel-sharp */
@property(nonatomic,weak) id panelTarget;
- (void)releaseInput;
@end
@implementation TPActionButton
- (void)setHighlighted:(BOOL)h { [super setHighlighted:h]; [self setNeedsDisplay]; }
- (void)drawRect:(CGRect)rect {
    CGContextRef c=UIGraphicsGetCurrentContext(); CGRect r=CGRectInset(self.bounds,1,1); BOOL down=self.highlighted||self.inputHeld;
    if(self.square){
        UIImage *slot=TPIcon(down?@"Inventory_Back14":@"Inventory_Back");
        CGContextSetInterpolationQuality(c,kCGInterpolationNone);
        if(slot)[slot drawInRect:r]; else {CGContextSetFillColorWithColor(c,TPNavy().CGColor);CGContextFillRect(c,r);}
    } else TPDrawDisc(c,r,self.body?:TPNavy(),down);
    if(self.kind==TPKindAimToggle){ /* aim & use: filled crosshair; aim only: hollow */
        CGFloat m=r.size.width; CGPoint o=CGPointMake(CGRectGetMidX(r),CGRectGetMidY(r));
        CGContextSetStrokeColorWithColor(c,UIColor.whiteColor.CGColor);CGContextSetLineWidth(c,MAX(2,m*0.06));
        CGContextStrokeEllipseInRect(c,CGRectMake(o.x-m*0.22,o.y-m*0.22,m*0.44,m*0.44));
        for(int i=0;i<4;i++){CGFloat dx=(i==0?1:i==1?-1:0),dy=(i==2?1:i==3?-1:0);CGContextMoveToPoint(c,o.x+dx*m*0.12,o.y+dy*m*0.12);CGContextAddLineToPoint(c,o.x+dx*m*0.34,o.y+dy*m*0.34);}
        CGContextStrokePath(c);
        if(TPAimUse()){CGContextSetFillColorWithColor(c,UIColor.whiteColor.CGColor);CGContextFillEllipseInRect(c,CGRectMake(o.x-m*0.07,o.y-m*0.07,m*0.14,m*0.14));}
        return;
    }
    if(self.sprite){
        CGSize s=self.sprite.size; CGFloat box=r.size.width*(self.square?0.62:0.58), k=box/MAX(s.width,s.height);
        CGRect ir=CGRectMake(CGRectGetMidX(r)-s.width*k/2,CGRectGetMidY(r)-s.height*k/2,s.width*k,s.height*k);
        CGContextSetInterpolationQuality(c,kCGInterpolationNone);[self.sprite drawInRect:ir];
    } else if(self.glyph){
        UIImage *g=[[UIImage systemImageNamed:self.glyph withConfiguration:[UIImageSymbolConfiguration configurationWithWeight:UIImageSymbolWeightBlack]] imageWithTintColor:UIColor.whiteColor renderingMode:UIImageRenderingModeAlwaysOriginal];
        CGSize s=g.size; CGFloat box=r.size.width*0.5, k=box/MAX(s.width,s.height);
        [g drawInRect:CGRectMake(CGRectGetMidX(r)-s.width*k/2,CGRectGetMidY(r)-s.height*k/2,s.width*k,s.height*k)];
    }
}
- (void)pressInput {
    if(TPEditing||self.inputHeld)return;
    switch(self.kind){
        case TPKindAimToggle: [NSUserDefaults.standardUserDefaults setBool:!TPAimUse() forKey:@"TPAimUse"]; [self setNeedsDisplay]; return;
        case TPKindPanel: { id t=self.panelTarget; SEL sel=NSSelectorFromString(@"togglePanel"); if([t respondsToSelector:sel])((void(*)(id,SEL))objc_msgSend)(t,sel); return; }
        case TPKindZoomIn: TPZoomSteps++; return;
        case TPKindZoomOut: TPZoomSteps--; return;
        default: break;
    }
    self.inputHeld=YES;[self setNeedsDisplay];
    if(self.mouseButton)SDL_SendMouseButton(self.sdl,0,SDL_PRESSED,(Uint8)self.mouseButton);
    else SDL_SendKeyboardKey(SDL_PRESSED,self.scan);
    if(self.kind==TPKindTap){ __weak TPActionButton *w=self; dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(0.08*NSEC_PER_SEC)),dispatch_get_main_queue(),^{[w releaseInput];}); }
}
- (void)tapRelease { if(self.kind!=TPKindTap)[self releaseInput]; }
- (void)releaseInput {
    if(!self.inputHeld)return;self.inputHeld=NO;[self setNeedsDisplay];
    if(self.mouseButton)SDL_SendMouseButton(self.sdl,0,SDL_RELEASED,(Uint8)self.mouseButton);
    else SDL_SendKeyboardKey(SDL_RELEASED,self.scan);
}
@end

/* Sticks. Move: WASD; double-tap up = jump (Space held while up), double-tap left/right/down = Terraria's own
 * double-tap A/D (dash) and S (set bonus) because the stick sends those keys. Aim: moves the cursor around the
 * player; in Aim & Use mode it also holds the left mouse button while pushed past the dead zone. */
@interface TPStick : UIView
@property(nonatomic,assign) SDL_Window *sdl;
@property(nonatomic,assign) BOOL aiming;
@property(nonatomic,assign) CGPoint vector;
@property(nonatomic,assign) unsigned directions;
@property(nonatomic,assign) double lastUp;
@property(nonatomic,assign) BOOL jumpHeld, useHeld;
- (void)resetInput;
@end
@implementation TPStick
- (void)drawRect:(CGRect)rect {
    CGContextRef c=UIGraphicsGetCurrentContext();CGFloat side=MIN(self.bounds.size.width,self.bounds.size.height);
    CGRect ring=CGRectInset(self.bounds,3,3);
    CGContextSetFillColorWithColor(c,[UIColor colorWithWhite:0 alpha:0.12].CGColor);CGContextFillEllipseInRect(c,ring);
    CGContextSetStrokeColorWithColor(c,TPRim().CGColor);CGContextSetLineWidth(c,MAX(2.5,side*0.025));CGContextStrokeEllipseInRect(c,ring);
    CGFloat d=side*0.48;CGPoint o=CGPointMake(side/2+self.vector.x*side*0.26,side/2+self.vector.y*side*0.26);
    CGRect knob=CGRectMake(o.x-d/2,o.y-d/2,d,d); TPDrawDisc(c,knob,TPNavy(),NO);
    CGContextSetFillColorWithColor(c,UIColor.whiteColor.CGColor);
    if(!self.aiming){ /* four d-pad pips */
        CGFloat t=d*0.11,off=d*0.30;
        for(int i=0;i<4;i++){ CGFloat ang=i*M_PI_2; CGPoint p=CGPointMake(o.x+cos(ang)*off,o.y+sin(ang)*off);
            CGContextMoveToPoint(c,p.x+cos(ang)*t,p.y+sin(ang)*t);
            CGContextAddLineToPoint(c,p.x+cos(ang+2.2)*t,p.y+sin(ang+2.2)*t);CGContextAddLineToPoint(c,p.x+cos(ang-2.2)*t,p.y+sin(ang-2.2)*t);CGContextClosePath(c);}
        CGContextFillPath(c);
    } else { /* aim reticle ticks + held-item hint */
        CGContextSetStrokeColorWithColor(c,UIColor.whiteColor.CGColor);CGContextSetLineWidth(c,MAX(2,d*0.07));
        for(int i=0;i<4;i++){CGFloat ang=i*M_PI_2;CGContextMoveToPoint(c,o.x+cos(ang)*d*0.30,o.y+sin(ang)*d*0.30);CGContextAddLineToPoint(c,o.x+cos(ang)*d*0.44,o.y+sin(ang)*d*0.44);}
        CGContextStrokePath(c);
        UIImage *sw=TPIcon(@"Item_190"); if(sw){CGContextSetInterpolationQuality(c,kCGInterpolationNone);[sw drawInRect:CGRectMake(o.x-d*0.26,o.y-d*0.26,d*0.52,d*0.52)];}
    }
}
- (void)updateTouch:(UITouch*)touch {
    if(TPEditing)return;
    CGPoint p=[touch locationInView:self];CGFloat radius=MAX(1,self.bounds.size.width*0.4);
    CGFloat x=(p.x-self.bounds.size.width/2)/radius,y=(p.y-self.bounds.size.height/2)/radius;
    CGFloat length=hypot(x,y);if(length>1){x/=length;y/=length;}
    self.vector=CGPointMake(x,y);[self setNeedsDisplay];
    if(self.aiming){
        if(length>0.15) SDL_SendMouseMotion(self.sdl,0,0,(int)(self.sdl->w*(0.5+x*0.30)),(int)(self.sdl->h*(0.5+y*0.35)));
        BOOL use=TPAimUse()&&length>0.35;
        if(use!=self.useHeld){self.useHeld=use;SDL_SendMouseButton(self.sdl,0,use?SDL_PRESSED:SDL_RELEASED,SDL_BUTTON_LEFT);}
    }else{
        unsigned next=(x < -0.25?1:0)|(x > 0.25?2:0)|(y < -0.45?4:0)|(y > 0.45?8:0);
        SDL_Scancode codes[]={SDL_SCANCODE_A,SDL_SCANCODE_D,SDL_SCANCODE_W,SDL_SCANCODE_S};
        for(int i=0;i<4;i++)if((next^(self.directions))&(1<<i))SDL_SendKeyboardKey((next&(1<<i))?SDL_PRESSED:SDL_RELEASED,codes[i]);
        if((next&4)&&!(self.directions&4)){ double now=CACurrentMediaTime(); if(now-self.lastUp<0.32&&!self.jumpHeld){self.jumpHeld=YES;SDL_SendKeyboardKey(SDL_PRESSED,SDL_SCANCODE_SPACE);} self.lastUp=now; }
        if(!(next&4)&&self.jumpHeld){self.jumpHeld=NO;SDL_SendKeyboardKey(SDL_RELEASED,SDL_SCANCODE_SPACE);}
        self.directions=next;
    }
}
- (void)touchesBegan:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event{[self updateTouch:touches.anyObject];}
- (void)touchesMoved:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event{[self updateTouch:touches.anyObject];}
- (void)touchesEnded:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event{[self resetInput];}
- (void)touchesCancelled:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)event{[self resetInput];}
- (void)resetInput {
    SDL_Scancode codes[]={SDL_SCANCODE_A,SDL_SCANCODE_D,SDL_SCANCODE_W,SDL_SCANCODE_S};
    for(int i=0;i<4;i++)if(self.directions&(1<<i))SDL_SendKeyboardKey(SDL_RELEASED,codes[i]);
    if(self.jumpHeld){self.jumpHeld=NO;SDL_SendKeyboardKey(SDL_RELEASED,SDL_SCANCODE_SPACE);}
    if(self.useHeld){self.useHeld=NO;SDL_SendMouseButton(self.sdl,0,SDL_RELEASED,SDL_BUTTON_LEFT);}
    self.directions=0;self.vector=CGPointZero;[self setNeedsDisplay];
}
@end

@interface TPTouchOverlay : UIView
@property(nonatomic,assign) SDL_Window *sdl;
@property(nonatomic,strong) UIButton *gear;
@property(nonatomic,strong) UIView *panel;
@property(nonatomic,strong) UISegmentedControl *mode;
@property(nonatomic,strong) UILabel *controllerLabel;
@property(nonatomic,strong) TPStick *moveStick;
@property(nonatomic,strong) TPStick *aimStick;
@property(nonatomic,strong) NSMutableArray<TPActionButton*> *playButtons;
@property(nonatomic,strong) NSMutableArray<TPActionButton*> *menuButtons;
@property(nonatomic,strong) NSTimer *timer;
@property(nonatomic,strong) UISegmentedControl *renderer;
@property(nonatomic,strong) UISwitch *hudSwitch;
@property(nonatomic,assign) int hudApplied;
@property(nonatomic,strong) UILabel *perfLabel;
@property(nonatomic,strong) UISwitch *perfSwitch, *hideGearSwitch, *lazySwitch, *bakedSwitch;
@property(nonatomic,strong) UISegmentedControl *fpsCap;
@property(nonatomic,strong) UIView *editBar;
@property(nonatomic,strong) UISlider *opacitySlider;
@property(nonatomic,weak) UIView *selected;
@property(nonatomic,strong) NSMutableDictionary<NSString*,NSValue*> *defaultFrames;
@property(nonatomic,assign) uint64_t lastPresents;
@property(nonatomic,assign) double lastPerfTime;
@property(nonatomic,assign) BOOL threeFingerInstalled;
@property(nonatomic,assign) int ticks;
- (instancetype)initWithSDLWindow:(SDL_Window*)window;
- (void)shutdown;
@end
@implementation TPTouchOverlay
- (TPActionButton*)control:(NSString*)label kind:(TPKind)kind scan:(SDL_Scancode)scan mouse:(int)mouse square:(BOOL)square color:(UIColor*)color sprite:(NSString*)sprite glyph:(NSString*)glyph {
    TPActionButton *b=[TPActionButton buttonWithType:UIButtonTypeCustom];b.sdl=self.sdl;b.scan=scan;b.mouseButton=mouse;b.kind=kind;b.square=square;b.body=color;b.glyph=glyph;b.sprite=sprite?TPIcon(sprite):nil;if(!b.sprite && !b.glyph)b.glyph=@"circle.fill";b.panelTarget=self;
    b.accessibilityLabel=label;b.backgroundColor=UIColor.clearColor;b.contentMode=UIViewContentModeRedraw;b.opaque=NO;
    [b addTarget:b action:@selector(pressInput) forControlEvents:UIControlEventTouchDown];
    [b addTarget:b action:@selector(tapRelease) forControlEvents:UIControlEventTouchUpInside|UIControlEventTouchUpOutside|UIControlEventTouchCancel];
    [self addSubview:b];return b;
}
- (instancetype)initWithSDLWindow:(SDL_Window*)window {
    { static BOOL watching = NO; if (!watching) { watching = YES; SDL_AddEventWatch(TPEventWatch, NULL); SDL_UIKit_Trace("v28 event watch installed"); } }
    if((self=[super initWithFrame:CGRectZero])){
        self.sdl=window;self.backgroundColor=UIColor.clearColor;self.multipleTouchEnabled=YES;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
        self.playButtons=[NSMutableArray new];self.menuButtons=[NSMutableArray new];
        self.moveStick=[TPStick new];self.moveStick.sdl=window;self.moveStick.backgroundColor=UIColor.clearColor;[self addSubview:self.moveStick];
        self.aimStick=[TPStick new];self.aimStick.sdl=window;self.aimStick.aiming=YES;self.aimStick.backgroundColor=UIColor.clearColor;[self addSubview:self.aimStick];
        UIColor *green=[UIColor colorWithRed:0.24 green:0.52 blue:0.30 alpha:1], *teal=[UIColor colorWithRed:0.34 green:0.56 blue:0.68 alpha:1];
        #define TPC(l,k,sc,m,sq,col,spr,gl) [self.playButtons addObject:[self control:l kind:k scan:sc mouse:m square:sq color:col sprite:spr glyph:gl]]
        TPC(@"Inventory",TPKindHold,SDL_SCANCODE_ESCAPE,0,YES,nil,nil,@"square.grid.3x3.fill");
        TPC(@"Aim mode",TPKindAimToggle,SDL_SCANCODE_UNKNOWN,0,NO,nil,nil,nil);
        TPC(@"Smart interact",TPKindHold,SDL_SCANCODE_UNKNOWN,SDL_BUTTON_RIGHT,NO,nil,nil,@"hand.point.up.left.fill");
        TPC(@"Auto select",TPKindHold,SDL_SCANCODE_LSHIFT,0,NO,nil,@"Item_8",nil);
        TPC(@"Quick heal",TPKindHold,SDL_SCANCODE_H,0,NO,nil,@"Item_188",nil);
        TPC(@"Quick mana",TPKindHold,SDL_SCANCODE_J,0,NO,nil,@"Item_189",nil);
        TPC(@"Quick buff",TPKindHold,SDL_SCANCODE_B,0,NO,nil,@"Item_292",nil);
        TPC(@"Zoom out",TPKindZoomOut,SDL_SCANCODE_UNKNOWN,0,YES,nil,nil,@"minus.magnifyingglass");
        TPC(@"Zoom in",TPKindZoomIn,SDL_SCANCODE_UNKNOWN,0,YES,nil,nil,@"plus.magnifyingglass");
        TPC(@"Settings",TPKindPanel,SDL_SCANCODE_UNKNOWN,0,YES,nil,nil,@"gearshape.fill");
        TPC(@"Smart cursor",TPKindTap,SDL_SCANCODE_LCTRL,0,NO,nil,nil,@"square");
        TPC(@"Mount",TPKindHold,SDL_SCANCODE_R,0,NO,green,@"Item_2430",nil);
        TPC(@"Jump",TPKindHold,SDL_SCANCODE_SPACE,0,NO,teal,nil,@"chevron.up.2");
        TPC(@"Grapple",TPKindHold,SDL_SCANCODE_E,0,NO,green,@"Item_84",nil);
        TPC(@"Secondary fire",TPKindHold,SDL_SCANCODE_UNKNOWN,SDL_BUTTON_RIGHT,NO,nil,@"Item_3507",nil);
        TPC(@"Use item",TPKindHold,SDL_SCANCODE_UNKNOWN,SDL_BUTTON_LEFT,NO,nil,@"Item_190",nil);
        TPC(@"Map",TPKindHold,SDL_SCANCODE_M,0,YES,nil,nil,@"map.fill");
        TPC(@"Minimap",TPKindTap,SDL_SCANCODE_TAB,0,YES,nil,nil,@"rectangle.inset.topright.filled");
        #undef TPC
        [self.menuButtons addObject:[self control:@"Confirm" kind:TPKindHold scan:SDL_SCANCODE_RETURN mouse:0 square:YES color:nil sprite:nil glyph:@"checkmark"]];
        [self.menuButtons addObject:[self control:@"Back" kind:TPKindHold scan:SDL_SCANCODE_ESCAPE mouse:0 square:YES color:nil sprite:nil glyph:@"arrow.uturn.backward"]];
        self.gear=[UIButton buttonWithType:UIButtonTypeSystem];[self.gear setImage:[UIImage systemImageNamed:@"gearshape.fill"] forState:UIControlStateNormal];self.gear.tintColor=UIColor.whiteColor;self.gear.backgroundColor=[UIColor colorWithWhite:0 alpha:0.5];self.gear.layer.cornerRadius=12;self.gear.accessibilityLabel=@"Port controls";[self.gear addTarget:self action:@selector(togglePanel) forControlEvents:UIControlEventTouchUpInside];[self addSubview:self.gear];
        self.moveStick.accessibilityLabel=@"Move stick";self.aimStick.accessibilityLabel=@"Aim stick";
        self.panel=[UIView new];self.panel.backgroundColor=[UIColor colorWithRed:0.06 green:0.10 blue:0.20 alpha:0.97];self.panel.layer.cornerRadius=16;self.panel.hidden=YES;self.panel.clipsToBounds=YES;[self addSubview:self.panel];
        UIScrollView *sv=[UIScrollView new];sv.tag=901;sv.alwaysBounceVertical=YES;[self.panel addSubview:sv];
        NSUserDefaults *d=NSUserDefaults.standardUserDefaults; __block CGFloat y=12;
        UILabel *title=[[UILabel alloc]initWithFrame:CGRectMake(18,y,300,28)];title.text=@"Controls & display";title.font=[UIFont boldSystemFontOfSize:20];title.textColor=UIColor.whiteColor;[sv addSubview:title];y+=38;
        self.mode=[[UISegmentedControl alloc]initWithItems:@[@"Menu",@"Play",@"Controller"]];self.mode.frame=CGRectMake(16,y,328,32);self.mode.selectedSegmentIndex=[d integerForKey:@"TerrariaPort.ControlMode"];if(self.mode.selectedSegmentIndex<0||self.mode.selectedSegmentIndex>2)self.mode.selectedSegmentIndex=0;[self.mode addTarget:self action:@selector(changeMode) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.mode];y+=40;
        self.controllerLabel=[[UILabel alloc]initWithFrame:CGRectMake(18,y,324,36)];self.controllerLabel.numberOfLines=2;self.controllerLabel.font=[UIFont systemFontOfSize:12];self.controllerLabel.textColor=UIColor.lightGrayColor;[sv addSubview:self.controllerLabel];y+=42;
        UIButton *edit=[UIButton buttonWithType:UIButtonTypeSystem];edit.frame=CGRectMake(16,y,328,36);[edit setTitle:@"Edit control layout (drag, resize, hide)" forState:UIControlStateNormal];edit.backgroundColor=[UIColor colorWithRed:0.15 green:0.35 blue:0.6 alpha:0.9];edit.layer.cornerRadius=10;[edit setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];[edit addTarget:self action:@selector(beginEditing) forControlEvents:UIControlEventTouchUpInside];[sv addSubview:edit];y+=46;
        UILabel *(^row)(NSString*)=^UILabel*(NSString *t){UILabel *l=[[UILabel alloc]initWithFrame:CGRectMake(18,y,190,31)];l.text=t;l.font=[UIFont boldSystemFontOfSize:14];l.textColor=UIColor.whiteColor;[sv addSubview:l];return l;};
        row(@"Frame rate cap");self.fpsCap=[[UISegmentedControl alloc]initWithItems:@[@"30",@"60",@"120",@"Max"]];self.fpsCap.frame=CGRectMake(150,y,194,31);
        { NSInteger cap=[d objectForKey:@"TPFrameCap"]?[d integerForKey:@"TPFrameCap"]:60; self.fpsCap.selectedSegmentIndex=cap==30?0:cap==60?1:cap==120?2:3; }
        [self.fpsCap addTarget:self action:@selector(changeFrameCap) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.fpsCap];y+=42;
        row(@"Performance overlay");self.perfSwitch=[UISwitch new];self.perfSwitch.frame=CGRectMake(294,y,51,31);self.perfSwitch.on=[d objectForKey:@"TPPerfOverlay"]?[d boolForKey:@"TPPerfOverlay"]:YES;[self.perfSwitch addTarget:self action:@selector(changePerf) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.perfSwitch];y+=40;
        row(@"Hide gear button");self.hideGearSwitch=[UISwitch new];self.hideGearSwitch.frame=CGRectMake(294,y,51,31);self.hideGearSwitch.on=[d boolForKey:@"TPHideGear"];[self.hideGearSwitch addTarget:self action:@selector(changeHideGear) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.hideGearSwitch];y+=36;
        UILabel *gh=[[UILabel alloc]initWithFrame:CGRectMake(18,y,324,18)];gh.text=@"When hidden: three-finger tap anywhere opens this menu.";gh.font=[UIFont systemFontOfSize:11];gh.textColor=UIColor.lightGrayColor;[sv addSubview:gh];y+=26;
        row(@"Renderer");self.renderer=[[UISegmentedControl alloc]initWithItems:@[@"Metal",@"OpenGL ES"]];self.renderer.frame=CGRectMake(150,y,194,31);
        self.renderer.selectedSegmentIndex=[([d stringForKey:@"TPRenderer"]?:@"metal") isEqualToString:@"metal"]?0:1;[self.renderer addTarget:self action:@selector(changeRenderer) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.renderer];y+=40;
        row(@"Metal HUD");self.hudSwitch=[UISwitch new];self.hudSwitch.frame=CGRectMake(294,y,51,31);self.hudSwitch.on=[d objectForKey:@"TPMetalHUD"]?[d boolForKey:@"TPMetalHUD"]:YES;[self.hudSwitch addTarget:self action:@selector(changeHUD) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.hudSwitch];y+=38;
        row(@"Low-memory textures");self.lazySwitch=[UISwitch new];self.lazySwitch.frame=CGRectMake(294,y,51,31);self.lazySwitch.on=[d objectForKey:@"TPLazyTextures"]?[d boolForKey:@"TPLazyTextures"]:YES;[self.lazySwitch addTarget:self action:@selector(changeLazy) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.lazySwitch];y+=36;
        UIView *lz=[[UILabel alloc]initWithFrame:CGRectMake(18,y,324,30)];((UILabel*)lz).numberOfLines=2;((UILabel*)lz).text=@"Loads Calamity images only when first shown (less RAM, less heat). Applies on next launch.";((UILabel*)lz).font=[UIFont systemFontOfSize:11];((UILabel*)lz).textColor=UIColor.lightGrayColor;[sv addSubview:lz];y+=36;
        row(@"AOT Calamity code");self.bakedSwitch=[UISwitch new];self.bakedSwitch.frame=CGRectMake(294,y,51,31);self.bakedSwitch.on=[d objectForKey:@"TPBakedIL"]?[d boolForKey:@"TPBakedIL"]:YES;[self.bakedSwitch addTarget:self action:@selector(changeBaked) forControlEvents:UIControlEventValueChanged];[sv addSubview:self.bakedSwitch];y+=36;
        { UILabel *bl=[[UILabel alloc]initWithFrame:CGRectMake(18,y,324,30)];bl.numberOfLines=2;bl.text=@"Runs Calamity's engine edits as compiled code (cooler, faster). Turn off if something breaks. Applies on next launch.";bl.font=[UIFont systemFontOfSize:11];bl.textColor=UIColor.lightGrayColor;[sv addSubview:bl];y+=36; }
        UILabel *fps=[[UILabel alloc]initWithFrame:CGRectMake(18,y,324,46)];fps.numberOfLines=3;fps.font=[UIFont systemFontOfSize:11];fps.textColor=UIColor.lightGrayColor;
        fps.text=@"Renderer applies on next launch. 120 FPS also needs Terraria Settings > Video > Frame Skip: Off. Lower caps run cooler.";[sv addSubview:fps];y+=52;
        UIButton *close=[UIButton buttonWithType:UIButtonTypeSystem];close.frame=CGRectMake(16,y,328,36);[close setTitle:@"Done" forState:UIControlStateNormal];[close addTarget:self action:@selector(togglePanel) forControlEvents:UIControlEventTouchUpInside];[sv addSubview:close];y+=48;
        sv.contentSize=CGSizeMake(360,y);
        self.perfLabel=[UILabel new];self.perfLabel.font=[UIFont monospacedDigitSystemFontOfSize:11 weight:UIFontWeightSemibold];self.perfLabel.textColor=UIColor.whiteColor;self.perfLabel.backgroundColor=[UIColor colorWithWhite:0 alpha:0.45];self.perfLabel.layer.cornerRadius=6;self.perfLabel.clipsToBounds=YES;self.perfLabel.textAlignment=NSTextAlignmentCenter;self.perfLabel.userInteractionEnabled=NO;[self addSubview:self.perfLabel];
        [self buildEditBar];
        [self applyFrameCap];
        __weak TPTouchOverlay *weak=self;self.timer=[NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer *t){[weak updateStatus];}];
        [NSTimer scheduledTimerWithTimeInterval:0.5 repeats:YES block:^(NSTimer *t){ if(!weak){[t invalidate];return;} [weak updatePerf]; }];[self changeMode];[self updateStatus];
    }return self;
}
- (BOOL)pointInside:(CGPoint)p withEvent:(UIEvent*)event {
    if(TPEditing)return YES; /* editor owns all touches; nothing reaches the game */
    for(UIView *v in self.subviews)if(!v.hidden&&v.alpha>0.01&&v.userInteractionEnabled&&CGRectContainsPoint(v.frame,p))return YES;
    return NO;
}
- (void)releaseInputs { [self.moveStick resetInput];[self.aimStick resetInput];for(TPActionButton *b in self.playButtons)[b releaseInput];for(TPActionButton *b in self.menuButtons)[b releaseInput]; }
- (void)changeMode {
    [self releaseInputs];[NSUserDefaults.standardUserDefaults setInteger:self.mode.selectedSegmentIndex forKey:@"TerrariaPort.ControlMode"];
    [self applyVisibility];
}
- (NSArray<UIView*>*)allControls { NSMutableArray *a=[NSMutableArray arrayWithObjects:self.moveStick,self.aimStick,nil];[a addObjectsFromArray:self.playButtons];[a addObjectsFromArray:self.menuButtons];return a; }
- (BOOL)modeShows:(UIView*)v { NSInteger n=self.mode.selectedSegmentIndex; if([self.menuButtons containsObject:(id)v])return n==0; return n==1; }
- (NSMutableDictionary*)layoutStore { return [([NSUserDefaults.standardUserDefaults dictionaryForKey:@"TPLayout2"]?:@{}) mutableCopy]; }
- (BOOL)userHidden:(UIView*)v { NSNumber *h=[[self layoutStore][v.accessibilityLabel] objectForKey:@"h"]; if(h)return h.boolValue; return [@[@"Use item"] containsObject:v.accessibilityLabel]; }
- (float)controlOpacity { NSUserDefaults *d=NSUserDefaults.standardUserDefaults; return [d objectForKey:@"TPOpacity"]?[d floatForKey:@"TPOpacity"]:0.85f; }
- (void)applyVisibility {
    float op=[self controlOpacity];
    for(UIView *v in [self allControls]){
        BOOL show=[self modeShows:v], uh=[self userHidden:v];
        if(TPEditing){v.hidden=!show;v.alpha=uh?0.3:op;}
        else {v.hidden=!show||uh;v.alpha=op;}
        v.layer.borderColor=UIColor.systemYellowColor.CGColor;
        v.layer.borderWidth=(TPEditing&&v==self.selected)?3:0;
    }
    TPActionButton *settings=nil; for(TPActionButton *b in self.playButtons)if([b.accessibilityLabel isEqualToString:@"Settings"])settings=b;
    self.gear.hidden=TPEditing||(self.hideGearSwitch.on)||(settings&&!settings.hidden);
    self.perfLabel.hidden=!self.perfSwitch.on;
}
- (void)changeRenderer {
    [NSUserDefaults.standardUserDefaults setObject:self.renderer.selectedSegmentIndex==0?@"metal":@"opengl" forKey:@"TPRenderer"];
    [NSUserDefaults.standardUserDefaults setBool:NO forKey:@"TPMetalPending"]; [NSUserDefaults.standardUserDefaults synchronize];
    SDL_UIKit_Trace(self.renderer.selectedSegmentIndex==0?"renderer setting -> metal (next launch)":"renderer setting -> opengl (next launch)");
}
- (void)changeHUD {
    [NSUserDefaults.standardUserDefaults setBool:self.hudSwitch.on forKey:@"TPMetalHUD"]; [NSUserDefaults.standardUserDefaults synchronize];
    self.hudApplied=-1; [self applyHUD];
}
static CALayer *TPFindMetalLayer(CALayer *l) {
    if ([l isKindOfClass:NSClassFromString(@"CAMetalLayer")]) return l;
    for (CALayer *c in l.sublayers) { CALayer *f = TPFindMetalLayer(c); if (f) return f; }
    return nil;
}
- (void)applyHUD {
    CALayer *metal = self.window ? TPFindMetalLayer(self.window.layer) : nil;
    int want = self.hudSwitch.on ? 1 : 0;
    if (!metal || self.hudApplied == want) return;
    SEL sel = NSSelectorFromString(@"setDeveloperHUDProperties:");
    if ([metal respondsToSelector:sel]) {
        [metal setValue:(want ? @{@"mode": @"default"} : nil) forKey:@"developerHUDProperties"];
        self.hudApplied = want;
        SDL_UIKit_Trace(want ? "metal HUD: enabled on CAMetalLayer" : "metal HUD: disabled on CAMetalLayer");
    }
}
- (void)togglePanel { if(TPEditing)return; [self releaseInputs];self.panel.hidden=!self.panel.hidden;[self bringSubviewToFront:self.panel]; }
- (void)updateStatus {
    /* v28 focus repair: UIKit says the app is active, but the game last saw FOCUS_LOST (its matching FOCUS_GAINED never
     * arrived). FNA would keep Game.IsActive=false and Terraria stays paused + muted. Re-deliver focus. */
    if (self.sdl && UIApplication.sharedApplication.applicationState == UIApplicationStateActive) {
        if (TPLastFocus == -1 || !(SDL_GetWindowFlags(self.sdl) & SDL_WINDOW_INPUT_FOCUS)) {
            SDL_UIKit_Trace([[NSString stringWithFormat:@"focus repair: app active, lastFocus=%d inputFocus=%d keyboardFocus=%d -> FOCUS_GAINED",
                TPLastFocus, (SDL_GetWindowFlags(self.sdl) & SDL_WINDOW_INPUT_FOCUS) != 0, SDL_GetKeyboardFocus() == self.sdl] UTF8String]);
            SDL_SetKeyboardFocus(self.sdl);
            SDL_SendWindowEvent(self.sdl, SDL_WINDOWEVENT_FOCUS_GAINED, 0, 0);
            TPLastFocus = 1;
        }
    }
    NSArray<GCController*> *controllers=GCController.controllers;
    self.controllerLabel.text=controllers.count?[NSString stringWithFormat:@"Connected: %@\nUses the game's controller bindings.",controllers.firstObject.vendorName?:@"Controller"]:@"No physical controller connected.\nPair it in iPhone Bluetooth settings.";
    if (self.ticks == 20 && [NSUserDefaults.standardUserDefaults boolForKey:@"TPMetalPending"]) {
        [NSUserDefaults.standardUserDefaults setBool:NO forKey:@"TPMetalPending"]; [NSUserDefaults.standardUserDefaults synchronize];
        SDL_UIKit_Trace("renderer: Metal launch confirmed after 20 s");
    }
    if (self.hudApplied == 0 && self.ticks == 0) self.hudApplied = -1;
    [self applyHUD];
    if((self.ticks++%5)==0){
        { int ww=0,wh=0,dw=0,dh=0; if(self.sdl){SDL_GetWindowSize(self.sdl,&ww,&wh);SDL_GL_GetDrawableSize(self.sdl,&dw,&dh);}
          SDL_UIKit_Trace([[NSString stringWithFormat:@"state appState=%ld lastFocus=%d inputFocus=%d keyboardFocus=%d window=%dx%d drawable=%dx%d bounds=%@",
            (long)UIApplication.sharedApplication.applicationState, TPLastFocus, self.sdl ? (SDL_GetWindowFlags(self.sdl) & SDL_WINDOW_INPUT_FOCUS) != 0 : -1,
            SDL_GetKeyboardFocus() == self.sdl, ww, wh, dw, dh, NSStringFromCGRect(self.window.bounds)] UTF8String]); }
        AVAudioSession *audio=AVAudioSession.sharedInstance;
        SDL_UIKit_Trace([TPMemoryBreakdown() UTF8String]);
        SDL_UIKit_Trace([[NSString stringWithFormat:@"audio session category=%@ volume=%.2f route=%@; controllers=%lu SDL joysticks=%d",audio.category,audio.outputVolume,audio.currentRoute.outputs,(unsigned long)controllers.count,SDL_NumJoysticks()] UTF8String]);
        typedef void(*Stats)(uint32_t*,uint32_t*,uint32_t*,float*);Stats stats=(Stats)dlsym(RTLD_DEFAULT,"FAudio_IOS_GetDebugState");
        if(stats){uint32_t count=0,skips=0,clients=0;float peak=0;stats(&count,&skips,&clients,&peak);SDL_UIKit_Trace([[NSString stringWithFormat:@"audio mixer callbacks=%u oversized=%u clients=%u peak=%.6f",count,skips,clients,peak] UTF8String]);}
        typedef int(*Stats2)(char*,int);Stats2 stats2=(Stats2)dlsym(RTLD_DEFAULT,"FAudio_IOS_GetDebugState2");
        if(stats2){char line[3072];stats2(line,(int)sizeof line);SDL_UIKit_Trace([[NSString stringWithFormat:@"audio engines %s",line] UTF8String]);}
        else SDL_UIKit_Trace("audio engines: FAudio_IOS_GetDebugState2 not found via dlsym");
    }
}
- (void)layoutSubviews {
    [super layoutSubviews];UIEdgeInsets safe=self.safeAreaInsets;CGFloat w=self.bounds.size.width,h=self.bounds.size.height;
    if(w<10||h<10)return;
    CGFloat left=MAX(14,safe.left+8),right=w-MAX(14,safe.right+8),bottom=h-MAX(12,safe.bottom+6);
    /* defaults follow the official iOS Terraria arrangement (annotated-layout reference) */
    CGFloat top=MAX(8,safe.top+6),cx=w/2;
    NSMutableDictionary<NSString*,NSValue*> *def=[NSMutableDictionary new];
    #define D(l,x,y,s) def[l]=[NSValue valueWithCGRect:CGRectMake(x,y,s,s)]
    D(@"Move stick",left+54,bottom-142,134);
    D(@"Inventory",left,bottom-48,48); D(@"Aim mode",left+60,bottom-46,44);
    D(@"Smart interact",left+30,bottom-216,58); D(@"Auto select",left+2,bottom-150,44);
    D(@"Quick heal",left+204,bottom-206,58); D(@"Quick mana",left+216,bottom-138,46); D(@"Quick buff",left+206,bottom-56,48);
    D(@"Zoom out",cx-150,bottom-42,40); D(@"Zoom in",cx+110,bottom-42,40);
    D(@"Settings",right-48,bottom-48,48); D(@"Smart cursor",right-110,bottom-50,46);
    D(@"Mount",right-60,bottom-128,50); D(@"Jump",right-84,bottom-244,74);
    D(@"Aim stick",right-250,bottom-150,134);
    D(@"Grapple",right-318,bottom-236,62); D(@"Secondary fire",right-306,bottom-118,48); D(@"Use item",right-176,bottom-232,58);
    D(@"Map",right-136,top,40); D(@"Minimap",right-90,top,40);
    #undef D
    def[@"Confirm"]=[NSValue valueWithCGRect:CGRectMake(right-112,bottom-50,48,48)];def[@"Back"]=[NSValue valueWithCGRect:CGRectMake(right-56,bottom-50,48,48)];
    self.defaultFrames=def;
    NSDictionary *store=[self layoutStore];
    for(UIView *v in [self allControls]){
        CGRect f=[def[v.accessibilityLabel] CGRectValue]; NSDictionary *o=store[v.accessibilityLabel];
        if(o){CGFloat sc=[o[@"s"] doubleValue]?:1;CGSize sz=CGSizeMake(f.size.width*sc,f.size.height*sc);
              f=CGRectMake([o[@"x"] doubleValue]*w-sz.width/2,[o[@"y"] doubleValue]*h-sz.height/2,sz.width,sz.height);}
        v.frame=f;v.layer.cornerRadius=([v isKindOfClass:[TPActionButton class]]&&((TPActionButton*)v).square)?6:MIN(f.size.width,f.size.height)/2;[v setNeedsDisplay];
    }
    self.gear.frame=CGRectMake(right-38,MAX(8,safe.top+6),36,32);self.panel.frame=CGRectMake((w-360)/2,MAX(8,(h-392)/2),360,MIN(392,h-16));
    [self.panel viewWithTag:901].frame=self.panel.bounds;
    self.perfLabel.frame=CGRectMake((w-480)/2,MAX(4,safe.top+2),480,20);
    self.editBar.frame=CGRectMake((w-560)/2,MAX(6,safe.top+4),560,82);
    [self applyVisibility];
}
/* ---- layout editor ---- */
- (UIButton*)barButton:(NSString*)t action:(SEL)a x:(CGFloat)x w:(CGFloat)bw {
    UIButton *b=[UIButton buttonWithType:UIButtonTypeSystem];b.frame=CGRectMake(x,8,bw,32);[b setTitle:t forState:UIControlStateNormal];[b setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
    b.backgroundColor=[UIColor colorWithRed:0.15 green:0.35 blue:0.6 alpha:0.95];b.layer.cornerRadius=8;b.titleLabel.font=[UIFont boldSystemFontOfSize:13];[b addTarget:self action:a forControlEvents:UIControlEventTouchUpInside];[self.editBar addSubview:b];return b;
}
- (void)buildEditBar {
    self.editBar=[UIView new];self.editBar.backgroundColor=[UIColor colorWithRed:0.04 green:0.08 blue:0.16 alpha:0.94];self.editBar.layer.cornerRadius=12;self.editBar.hidden=YES;[self addSubview:self.editBar];
    [self barButton:@"Smaller" action:@selector(editSmaller) x:8 w:84];[self barButton:@"Bigger" action:@selector(editBigger) x:98 w:84];
    [self barButton:@"Hide / Show" action:@selector(editToggleHidden) x:188 w:110];[self barButton:@"Reset all" action:@selector(editReset) x:304 w:110];
    [self barButton:@"Done" action:@selector(endEditing) x:420 w:132];
    UILabel *l=[[UILabel alloc]initWithFrame:CGRectMake(12,46,150,28)];l.text=@"Control opacity";l.textColor=UIColor.whiteColor;l.font=[UIFont systemFontOfSize:13];[self.editBar addSubview:l];
    self.opacitySlider=[[UISlider alloc]initWithFrame:CGRectMake(130,46,422,28)];self.opacitySlider.minimumValue=0.15;self.opacitySlider.maximumValue=1;self.opacitySlider.value=[self controlOpacity];
    [self.opacitySlider addTarget:self action:@selector(editOpacity) forControlEvents:UIControlEventValueChanged];[self.editBar addSubview:self.opacitySlider];
}
- (void)beginEditing {
    [self releaseInputs];TPEditing=YES;self.panel.hidden=YES;self.editBar.hidden=NO;[self bringSubviewToFront:self.editBar];
    for(UIView *v in [self allControls]){
        if(![objc_getAssociatedObject(v,"tpGestures") boolValue]){
            UIPanGestureRecognizer *pan=[[UIPanGestureRecognizer alloc]initWithTarget:self action:@selector(editPan:)];[v addGestureRecognizer:pan];
            UITapGestureRecognizer *tap=[[UITapGestureRecognizer alloc]initWithTarget:self action:@selector(editTap:)];[v addGestureRecognizer:tap];
            objc_setAssociatedObject(v,"tpGestures",@YES,OBJC_ASSOCIATION_RETAIN_NONATOMIC);
        }
        for(UIGestureRecognizer *g in v.gestureRecognizers)if([g isKindOfClass:[UIPanGestureRecognizer class]]||[g isKindOfClass:[UITapGestureRecognizer class]])g.enabled=YES;
    }
    SDL_UIKit_Trace("layout editor: begin");[self applyVisibility];
}
- (void)endEditing {
    TPEditing=NO;self.editBar.hidden=YES;self.selected=nil;
    for(UIView *v in [self allControls])for(UIGestureRecognizer *g in v.gestureRecognizers)if([g isKindOfClass:[UIPanGestureRecognizer class]]||[g isKindOfClass:[UITapGestureRecognizer class]])g.enabled=NO;
    SDL_UIKit_Trace("layout editor: done");[self setNeedsLayout];[self applyVisibility];
}
- (void)saveControl:(UIView*)v hidden:(NSNumber*)hidden {
    CGFloat w=self.bounds.size.width,h=self.bounds.size.height; CGRect def=[self.defaultFrames[v.accessibilityLabel] CGRectValue];
    NSMutableDictionary *store=[self layoutStore]; NSMutableDictionary *o=[(store[v.accessibilityLabel]?:@{}) mutableCopy];
    o[@"x"]=@(CGRectGetMidX(v.frame)/w);o[@"y"]=@(CGRectGetMidY(v.frame)/h);o[@"s"]=@(def.size.width>0?v.frame.size.width/def.size.width:1);
    if(hidden)o[@"h"]=hidden; store[v.accessibilityLabel]=o;
    [NSUserDefaults.standardUserDefaults setObject:store forKey:@"TPLayout2"];[NSUserDefaults.standardUserDefaults synchronize];
}
- (void)editPan:(UIPanGestureRecognizer*)g {
    if(!TPEditing)return; UIView *v=g.view; self.selected=v; CGPoint t=[g translationInView:self];
    CGPoint c=CGPointMake(v.center.x+t.x,v.center.y+t.y); CGFloat hw=v.bounds.size.width/2,hh=v.bounds.size.height/2;
    c.x=MAX(hw,MIN(self.bounds.size.width-hw,c.x));c.y=MAX(hh,MIN(self.bounds.size.height-hh,c.y));v.center=c;[g setTranslation:CGPointZero inView:self];
    if(g.state==UIGestureRecognizerStateEnded||g.state==UIGestureRecognizerStateCancelled)[self saveControl:v hidden:nil];
    [self applyVisibility];
}
- (void)editTap:(UITapGestureRecognizer*)g { if(!TPEditing)return; self.selected=g.view; [self applyVisibility]; }
- (void)scaleSelected:(CGFloat)k {
    UIView *v=self.selected; if(!v)return; CGPoint c=v.center; CGSize sz=CGSizeMake(MAX(30,MIN(220,v.bounds.size.width*k)),MAX(30,MIN(220,v.bounds.size.height*k)));
    v.frame=CGRectMake(c.x-sz.width/2,c.y-sz.height/2,sz.width,sz.height);v.layer.cornerRadius=MIN(sz.width,sz.height)/2;[v setNeedsDisplay];[self saveControl:v hidden:nil];[self applyVisibility];
}
- (void)editSmaller { [self scaleSelected:0.88]; }
- (void)editBigger { [self scaleSelected:1.14]; }
- (void)editToggleHidden { UIView *v=self.selected; if(!v)return; [self saveControl:v hidden:@(![self userHidden:v])]; [self applyVisibility]; }
- (void)editReset { [NSUserDefaults.standardUserDefaults removeObjectForKey:@"TPLayout2"];[NSUserDefaults.standardUserDefaults removeObjectForKey:@"TPOpacity"];self.opacitySlider.value=[self controlOpacity];self.selected=nil;[self setNeedsLayout];[self layoutIfNeeded]; }
- (void)editOpacity { [NSUserDefaults.standardUserDefaults setFloat:self.opacitySlider.value forKey:@"TPOpacity"];[self applyVisibility]; }
/* ---- settings ---- */
- (void)applyFrameCap {
    NSUserDefaults *d=NSUserDefaults.standardUserDefaults; NSInteger cap=[d objectForKey:@"TPFrameCap"]?[d integerForKey:@"TPFrameCap"]:60;
    typedef void(*SetCap)(int);SetCap f=(SetCap)dlsym(RTLD_DEFAULT,"FNA3D_TP_SetFrameCap");
    if(f){f((int)cap);SDL_UIKit_Trace([[NSString stringWithFormat:@"frame cap -> %ld",(long)cap] UTF8String]);} else SDL_UIKit_Trace("frame cap: FNA3D_TP_SetFrameCap not found");
}
- (void)changeFrameCap { NSInteger caps[]={30,60,120,0}; [NSUserDefaults.standardUserDefaults setInteger:caps[self.fpsCap.selectedSegmentIndex] forKey:@"TPFrameCap"]; [self applyFrameCap]; }
- (void)changePerf { [NSUserDefaults.standardUserDefaults setBool:self.perfSwitch.on forKey:@"TPPerfOverlay"]; [self applyVisibility]; }
- (void)changeBaked { [NSUserDefaults.standardUserDefaults setBool:self.bakedSwitch.on forKey:@"TPBakedIL"]; [NSUserDefaults.standardUserDefaults synchronize]; }
- (void)changeLazy { [NSUserDefaults.standardUserDefaults setBool:self.lazySwitch.on forKey:@"TPLazyTextures"]; [NSUserDefaults.standardUserDefaults synchronize]; }
- (void)changeHideGear { [NSUserDefaults.standardUserDefaults setBool:self.hideGearSwitch.on forKey:@"TPHideGear"]; [self applyVisibility]; }
- (void)threeFingerTap:(UITapGestureRecognizer*)g { if(g.state==UIGestureRecognizerStateRecognized&&!TPEditing){[self togglePanel];} }
- (void)updatePerf {
    if(!self.threeFingerInstalled&&self.superview){UITapGestureRecognizer *t=[[UITapGestureRecognizer alloc]initWithTarget:self action:@selector(threeFingerTap:)];t.numberOfTouchesRequired=3;t.cancelsTouchesInView=NO;t.delaysTouchesEnded=NO;[self.superview addGestureRecognizer:t];self.threeFingerInstalled=YES;}
    if(!self.perfSwitch.on){self.perfLabel.hidden=YES;return;}
    typedef uint64_t(*Count)(void);typedef double(*Ms)(void);static Count count;static Ms ms;if(!count)count=(Count)dlsym(RTLD_DEFAULT,"FNA3D_TP_GetPresentCount");if(!ms)ms=(Ms)dlsym(RTLD_DEFAULT,"FNA3D_TP_GetLastFrameMs");
    double now=CACurrentMediaTime(); uint64_t c=count?count():0; double fps=(self.lastPerfTime>0&&now>self.lastPerfTime)?(c-self.lastPresents)/(now-self.lastPerfTime):0;
    self.lastPresents=c;self.lastPerfTime=now;
    self.perfLabel.hidden=NO;
    self.perfLabel.text=[NSString stringWithFormat:@"FPS %.0f · %.1f ms · CPU %.0f%% · RAM %.2f GB (GPU %.2f) · %s · %s",fps,ms?ms():0,TPCPUPercent(),TPAppFootprintGB(),TPGraphicsGB(),TPThermalName(),getenv("FNA3D_FORCE_DRIVER")&&!strcmp(getenv("FNA3D_FORCE_DRIVER"),"Vulkan")?"Metal":"OpenGL ES"];
    [self bringSubviewToFront:self.perfLabel];
}
- (void)shutdown { [self.timer invalidate];self.timer=nil;[self releaseInputs]; }
@end
