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

// Keep these values in sync with TrayState in MacTray.cs.
typedef NS_ENUM(int, TrayState) { TrayIdle, TrayDisconnected, TrayScanning, TraySyncing };

static NSImage *trayIcon(TrayState state) {
    NSImage *image = [NSImage imageWithSize:NSMakeSize(22, 20) flipped:YES drawingHandler:^BOOL(NSRect bounds) {
        CGContextRef context = NSGraphicsContext.currentContext.CGContext;
        CGContextBeginTransparencyLayer(context, NULL);
        CGContextSetRGBStrokeColor(context, 0, 0, 0, 1);
        CGContextSetLineWidth(context, 1.6);
        CGContextSetLineCap(context, kCGLineCapRound);
        CGContextSetLineJoin(context, kCGLineJoinRound);

        CGContextMoveToPoint(context, 6.1, 15.7);
        CGContextAddCurveToPoint(context, 0.4, 15.7, 0.1, 7.6, 5.8, 7.1);
        CGContextAddCurveToPoint(context, 7.3, 1.2, 15.5, 1.2, 17, 7.5);
        CGContextAddCurveToPoint(context, 22.5, 8, 22.2, 15.7, 16.8, 15.7);
        CGContextClosePath(context);
        CGContextStrokePath(context);

        CGMutablePathRef badge = CGPathCreateMutable();
        switch (state) {
            case TrayScanning:
                CGPathAddEllipseInRect(badge, NULL, CGRectMake(10.7, 9, 6.6, 6.6));
                CGPathMoveToPoint(badge, NULL, 16.5, 14.8);
                CGPathAddLineToPoint(badge, NULL, 20, 18.3);
                break;
            case TraySyncing:
                CGPathMoveToPoint(badge, NULL, 12, 18);
                CGPathAddLineToPoint(badge, NULL, 12, 10.4);
                CGPathMoveToPoint(badge, NULL, 9.7, 12.7);
                CGPathAddLineToPoint(badge, NULL, 12, 10.4);
                CGPathAddLineToPoint(badge, NULL, 14.3, 12.7);
                CGPathMoveToPoint(badge, NULL, 18, 10.4);
                CGPathAddLineToPoint(badge, NULL, 18, 18);
                CGPathMoveToPoint(badge, NULL, 15.7, 15.7);
                CGPathAddLineToPoint(badge, NULL, 18, 18);
                CGPathAddLineToPoint(badge, NULL, 20.3, 15.7);
                break;
            case TrayDisconnected:
                CGPathMoveToPoint(badge, NULL, 4.1, 2.7);
                CGPathAddLineToPoint(badge, NULL, 18.6, 17.9);
                break;
            case TrayIdle:
                break;
        }

        // Leave a transparent gap wherever the badge crosses the cloud.
        CGContextSetBlendMode(context, kCGBlendModeClear);
        CGContextSetLineWidth(context, 4.1);
        CGContextAddPath(context, badge);
        CGContextStrokePath(context);
        CGContextSetBlendMode(context, kCGBlendModeNormal);
        CGContextSetLineWidth(context, 1.6);
        CGContextAddPath(context, badge);
        CGContextStrokePath(context);
        CGPathRelease(badge);
        CGContextEndTransparencyLayer(context);
        return YES;
    }];
    image.template = YES;
    return image;
}

void cloudreve_tray_update(TrayState state, const char *description) {
    NSStatusBarButton *button = tray.item.button;
    button.image = trayIcon(state);
    NSString *label = [NSString stringWithUTF8String:description];
    button.toolTip = label;
    [button setAccessibilityLabel:label];
}

void cloudreve_tray_start(TrayAction action, const char *show, const char *add, const char *settings, const char *quit) {
    tray = [CloudreveTray new];
    tray.action = action;
    tray.item = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
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
