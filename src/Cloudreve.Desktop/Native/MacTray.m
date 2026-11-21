#import <AppKit/AppKit.h>

typedef void (*TrayAction)(int);

@interface CloudreveTray : NSObject
@property(nonatomic, strong) NSStatusItem *item;
@property(nonatomic, strong) NSMenu *menu;
@property(nonatomic) TrayAction action;
@end

@implementation CloudreveTray
- (void)clicked:(id)sender {
    NSEvent *event = NSApp.currentEvent;
    if (event.type == NSEventTypeRightMouseUp || (event.modifierFlags & NSEventModifierFlagControl)) {
        [self.menu popUpMenuPositioningItem:nil atLocation:NSMakePoint(0, NSMinY(self.item.button.bounds)) inView:self.item.button];
    } else {
        self.action(0);
    }
}
- (void)selected:(NSMenuItem *)sender { self.action((int)sender.tag); }
@end

static CloudreveTray *tray;

void cloudreve_tray_start(TrayAction action, const char *show, const char *add, const char *settings, const char *quit) {
    tray = [CloudreveTray new];
    tray.action = action;
    tray.item = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
    NSImage *icon = [NSApp.applicationIconImage copy];
    icon.size = NSMakeSize(20, 20);
    tray.item.button.image = icon;
    tray.item.button.toolTip = @"Cloudreve";
    tray.item.button.target = tray;
    tray.item.button.action = @selector(clicked:);
    [tray.item.button sendActionOn:NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp];
    tray.menu = [NSMenu new];
    const char *titles[] = {show, add, settings, quit};
    for (NSInteger i = 0; i < 4; ++i) {
        NSMenuItem *entry = [tray.menu addItemWithTitle:[NSString stringWithUTF8String:titles[i]] action:@selector(selected:) keyEquivalent:@""];
        entry.target = tray;
        entry.tag = i;
    }
}

void cloudreve_tray_position(id handle) {
    NSWindow *popup = [handle isKindOfClass:NSView.class] ? [(NSView *)handle window] : handle;
    NSStatusBarButton *button = tray.item.button;
    if (!button.window || !popup) { return; }
    NSRect anchor = [button.window convertRectToScreen:[button convertRect:button.bounds toView:nil]];
    NSRect screen = button.window.screen.visibleFrame;
    CGFloat x = MAX(NSMinX(screen), MIN(NSMaxX(screen) - NSWidth(popup.frame), NSMidX(anchor) - NSWidth(popup.frame)/2));
    [popup setFrameTopLeftPoint:NSMakePoint(x, MIN(NSMaxY(screen), NSMinY(anchor)))];
}

void cloudreve_tray_stop(void) {
    if (tray) { [NSStatusBar.systemStatusBar removeStatusItem:tray.item]; tray = nil; }
}
