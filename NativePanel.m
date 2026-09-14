#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>
#import <Security/Security.h>
#import <ApplicationServices/ApplicationServices.h>

static void (*chatRequest)(const char *);
static NSMapTable<NSString *, WKWebView *> *requestViews;

void synor_chat_event(const char *json) {
    NSString *payload = [NSString stringWithUTF8String:json];
    dispatch_async(dispatch_get_main_queue(), ^{
        NSDictionary *event = [NSJSONSerialization JSONObjectWithData:[payload dataUsingEncoding:NSUTF8StringEncoding] options:0 error:nil];
        NSString *requestID = event[@"id"];
        if (!requestID) return;
        WKWebView *view = [requestViews objectForKey:requestID];
        if (event[@"done"] || event[@"error"] || event[@"settings"]) [requestViews removeObjectForKey:requestID];
        [view callAsyncJavaScript:@"window.dispatchEvent(new CustomEvent('synor-chat', {detail: JSON.parse(payload)}));"
            arguments:@{@"payload":payload} inFrame:nil inContentWorld:WKContentWorld.pageWorld completionHandler:nil];
    });
}

// 配置和密钥一起保存在当前用户的钥匙串，不写入网页存储或项目文件。
const char *synor_settings(const char *json) {
    static NSString *result;
    NSDictionary *query = @{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService:@"SynorTool.AI", (__bridge id)kSecAttrAccount:@"default"};
    OSStatus status;
    NSData *data;
    if (json) {
        data = [[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
        status = SecItemUpdate((__bridge CFDictionaryRef)query,
            (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData:data});
        if (status == errSecItemNotFound) {
            NSMutableDictionary *item = [query mutableCopy];
            item[(__bridge id)kSecValueData] = data;
            status = SecItemAdd((__bridge CFDictionaryRef)item, NULL);
        }
    } else {
        NSMutableDictionary *lookup = [query mutableCopy];
        lookup[(__bridge id)kSecReturnData] = @YES;
        CFTypeRef value = NULL;
        status = SecItemCopyMatching((__bridge CFDictionaryRef)lookup, &value);
        if (status == errSecSuccess) data = CFBridgingRelease(value);
        if (status == errSecItemNotFound) status = errSecSuccess;
    }
    NSDictionary *response = status == errSecSuccess
        ? @{@"settings": data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] ?: NSNull.null : NSNull.null}
        : @{@"error":[NSString stringWithFormat:@"无法访问钥匙串（%d），配置未保存。", (int)status]};
    result = [[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:response options:0 error:nil] encoding:NSUTF8StringEncoding];
    return result.UTF8String;
}

@interface SynorPanel : NSPanel
@property(weak) WKWebView *chatView;
@end
@implementation SynorPanel
- (BOOL)canBecomeKeyWindow { return YES; }
- (BOOL)performKeyEquivalent:(NSEvent *)event {
    NSEventModifierFlags modifiers = event.modifierFlags & NSEventModifierFlagDeviceIndependentFlagsMask;
    if (modifiers == NSEventModifierFlagCommand && [event.charactersIgnoringModifiers.lowercaseString isEqual:@"l"]) {
        [self.chatView evaluateJavaScript:@"if(!window.__SYNOR_TRANSLATION__) window.dispatchEvent(new Event('synor-clear-context'));" completionHandler:nil];
        return YES;
    }
    if (modifiers == NSEventModifierFlagCommand && [event.charactersIgnoringModifiers.lowercaseString isEqual:@"a"]) {
        // 无菜单面板显式转发全选操作，由 WebKit 按当前输入焦点处理。
        if ([NSApp sendAction:@selector(selectAll:) to:nil from:self]) return YES;
    }
    if (modifiers == NSEventModifierFlagCommand && [event.charactersIgnoringModifiers.lowercaseString isEqual:@"c"]) {
        // 交给当前原生响应者复制选区，保留 WebKit 对正文和输入框的选择处理。
        if ([NSApp sendAction:@selector(copy:) to:nil from:self]) return YES;
    }
    if (modifiers == NSEventModifierFlagCommand && [event.charactersIgnoringModifiers.lowercaseString isEqual:@"v"]) {
        // 无菜单的面板直接处理系统粘贴快捷键，不依赖 WebKit 生成文件粘贴事件。
        [self.chatView evaluateJavaScript:@"document.activeElement?.matches('textarea.input')"
            completionHandler:^(id isChat, NSError *error) {
                if (!error && [isChat boolValue]) {
                    [self.chatView evaluateJavaScript:@"window.webkit.messageHandlers.synor.postMessage({type:'paste'});" completionHandler:nil];
                } else {
                    [NSApp sendAction:@selector(paste:) to:nil from:self];
                }
            }];
        return YES;
    }
    return [super performKeyEquivalent:event];
}
- (void)cancelOperation:(id)sender {
    (void)sender;
    [self.chatView evaluateJavaScript:@"(()=>{const dialog=document.querySelector('dialog[open]'); if(dialog) dialog.close(); else window.webkit.messageHandlers.synor.postMessage({type:'close'});})();" completionHandler:nil];
}
@end

@interface PanelController : NSObject <NSApplicationDelegate, WKScriptMessageHandler, WKNavigationDelegate>
@property(strong) SynorPanel *panel;
@property(strong) NSURL *pageURL;
@property(strong) NSStatusItem *statusItem;
@property(strong) NSMenu *statusMenu;
@property(strong) id globalKeyMonitor;
@property(strong) id localKeyMonitor;
@property(strong) NSTimer *optionTimer;
@property BOOL optionHeld;
@property(strong) NSTimer *selectionTimer;
@property BOOL selectionIsSecure;
@property(strong) SynorPanel *translationPanel;
@property(strong) NSURL *translationURL;
@property BOOL translationReady;
@property(copy) NSString *pendingTranslation;
@end
@implementation PanelController
- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    (void)notification;
    self.panel = [[SynorPanel alloc] initWithContentRect:NSMakeRect(0, 0, 680, 740)
        styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel | NSWindowStyleMaskResizable
        backing:NSBackingStoreBuffered defer:NO];
    self.panel.contentMinSize = NSMakeSize(460, 500);
    self.panel.title = @"Synor · Web 聊天演示";
    self.panel.level = NSFloatingWindowLevel;
    self.panel.opaque = NO;
    self.panel.backgroundColor = NSColor.clearColor;
    self.panel.hasShadow = YES;
    self.panel.movableByWindowBackground = YES;
    self.panel.hidesOnDeactivate = NO;
    self.panel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces |
        NSWindowCollectionBehaviorFullScreenAuxiliary;
    NSGlassEffectView *glass = [[NSGlassEffectView alloc] initWithFrame:NSMakeRect(0, 0, 680, 740)];
    glass.style = NSGlassEffectViewStyleRegular;
    glass.cornerRadius = 18;
    glass.clipsToBounds = YES;
    NSView *content = [[NSView alloc] initWithFrame:glass.bounds];
    content.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    glass.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    glass.contentView = content;
    self.panel.contentView = glass;
    NSURL *iconURL = [[self.pageURL.URLByDeletingLastPathComponent URLByDeletingLastPathComponent]
        URLByAppendingPathComponent:@"Assets/MenuBarIcon.png"];
    NSImageView *logo = [[NSImageView alloc] initWithFrame:NSMakeRect(26, 689, 32, 32)];
    logo.image = [[NSImage alloc] initWithContentsOfURL:iconURL];
    self.statusItem = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
    NSImage *statusImage = [[NSImage alloc] initWithContentsOfURL:[iconURL.URLByDeletingLastPathComponent URLByAppendingPathComponent:@"MenuBarIcon.png"]];
    statusImage.size = NSMakeSize(18, 18);
    statusImage.template = YES;
    self.statusItem.button.image = statusImage;
    self.statusItem.button.toolTip = @"Synor · 点击显示，右键打开菜单";
    self.statusItem.button.accessibilityLabel = @"Synor";
    self.statusItem.button.target = self;
    self.statusItem.button.action = @selector(statusClicked:);
    [self.statusItem.button sendActionOn:NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp];
    self.statusMenu = [NSMenu new];
    NSMenuItem *show = [self.statusMenu addItemWithTitle:@"显示窗口" action:@selector(showWindow:) keyEquivalent:@""];
    show.target = self;
    NSMenuItem *translate = [self.statusMenu addItemWithTitle:@"快捷翻译" action:@selector(showTranslation:) keyEquivalent:@""];
    translate.target = self;
    NSMenuItem *hide = [self.statusMenu addItemWithTitle:@"隐藏窗口" action:@selector(hideWindow:) keyEquivalent:@""];
    hide.target = self;
    NSMenuItem *shortcut = [self.statusMenu addItemWithTitle:@"启用长按 Option 快捷键…" action:@selector(requestShortcutPermission:) keyEquivalent:@""];
    shortcut.target = self;
    [self.statusMenu addItem:NSMenuItem.separatorItem];
    NSMenuItem *quit = [self.statusMenu addItemWithTitle:@"退出 Synor" action:@selector(terminate:) keyEquivalent:@"q"];
    quit.target = NSApp;
    logo.imageScaling = NSImageScaleProportionallyUpOrDown;
    logo.autoresizingMask = NSViewMinYMargin;
    logo.wantsLayer = YES;
    logo.layer.cornerRadius = 8;
    logo.layer.cornerCurve = kCACornerCurveContinuous;
    logo.layer.masksToBounds = YES;
    logo.accessibilityLabel = @"Synor 品牌标志";
    [content addSubview:logo];
    NSTextField *brand = [NSTextField labelWithString:@"SYNOR"];
    brand.font = [NSFont systemFontOfSize:17 weight:NSFontWeightSemibold];
    [brand sizeToFit];
    brand.frame = NSMakeRect(66, 691 + (28 - NSHeight(brand.frame)) / 2,
                             NSWidth(brand.frame), NSHeight(brand.frame));
    brand.autoresizingMask = NSViewMinYMargin;
    [content addSubview:brand];
    NSTextField *subtitle = [NSTextField labelWithString:@"让想法更清晰，让表达更自然。"];
    subtitle.font = [NSFont systemFontOfSize:12];
    subtitle.textColor = NSColor.secondaryLabelColor;
    [subtitle sizeToFit];
    CGFloat subtitleX = NSMaxX(brand.frame) + 10;
    subtitle.frame = NSMakeRect(subtitleX, NSMidY(brand.frame) - NSHeight(subtitle.frame) / 2,
                                604 - subtitleX, NSHeight(subtitle.frame));
    subtitle.lineBreakMode = NSLineBreakByTruncatingTail;
    subtitle.autoresizingMask = NSViewWidthSizable | NSViewMinYMargin;
    [content addSubview:subtitle];
    NSTextField *escapeHint = [NSTextField labelWithString:@"Esc 隐藏"];
    escapeHint.font = [NSFont systemFontOfSize:11];
    escapeHint.textColor = NSColor.secondaryLabelColor;
    [escapeHint sizeToFit];
    escapeHint.frame = NSMakeRect(654 - NSWidth(escapeHint.frame),
        NSMidY(brand.frame) - NSHeight(escapeHint.frame) / 2,
        NSWidth(escapeHint.frame), NSHeight(escapeHint.frame));
    escapeHint.autoresizingMask = NSViewMinXMargin | NSViewMinYMargin;
    [content addSubview:escapeHint];
    NSRect subtitleFrame = subtitle.frame;
    subtitleFrame.size.width = NSMinX(escapeHint.frame) - 16 - NSMinX(subtitleFrame);
    subtitle.frame = subtitleFrame;


    WKWebViewConfiguration *configuration = [WKWebViewConfiguration new];
    configuration.websiteDataStore = WKWebsiteDataStore.nonPersistentDataStore;
    [configuration.userContentController addScriptMessageHandler:self name:@"synor"];
    WKWebView *web = [[WKWebView alloc] initWithFrame:NSMakeRect(20, 20, 640, 658) configuration:configuration];
    web.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    self.panel.chatView = web;

    web.navigationDelegate = self;
    web.wantsLayer = YES;
    web.layer.cornerRadius = 12;
    web.layer.cornerCurve = kCACornerCurveContinuous;
    web.layer.masksToBounds = YES;
    // WKWebView 的内部属性：演示中关闭默认底色，透出原生玻璃；系统升级需复核。
    [web setValue:@NO forKey:@"drawsBackground"];
    web.underPageBackgroundColor = NSColor.clearColor;
    [content addSubview:web];
    [web loadFileURL:self.pageURL allowingReadAccessToURL:self.pageURL.URLByDeletingLastPathComponent];
    __weak PanelController *weakSelf = self;
    NSEventMask shortcutEvents = NSEventMaskFlagsChanged | NSEventMaskKeyDown |
        NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown | NSEventMaskOtherMouseDown | NSEventMaskScrollWheel;
    self.localKeyMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:shortcutEvents handler:^NSEvent *(NSEvent *event) {
        [weakSelf handleOptionEvent:event];
        return event;
    }];
    [self refreshGlobalShortcut];
    if (!AXIsProcessTrusted()) {
        AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)@{(__bridge id)kAXTrustedCheckOptionPrompt:@YES});
    }
    [self.panel center];
    [self.panel makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}
// 只观察按键，不拦截、不记录输入；组合键或鼠标操作会取消本次长按。
- (void)handleOptionEvent:(NSEvent *)event {
    NSEventModifierFlags mask = NSEventModifierFlagOption | NSEventModifierFlagCommand |
        NSEventModifierFlagControl | NSEventModifierFlagShift | NSEventModifierFlagFunction;
    NSEventModifierFlags flags = event.modifierFlags & mask;
    if (!(flags & NSEventModifierFlagOption)) {
        self.optionHeld = NO;
        [self.optionTimer invalidate];
        self.optionTimer = nil;
        return;
    }
    if (event.type != NSEventTypeFlagsChanged || flags != NSEventModifierFlagOption) {
        self.optionHeld = YES;
        [self.optionTimer invalidate];
        self.optionTimer = nil;
        return;
    }
    if (self.optionHeld) return;
    self.optionHeld = YES;
    __weak PanelController *weakSelf = self;
    self.optionTimer = [NSTimer timerWithTimeInterval:0.5 repeats:NO block:^(NSTimer *timer) {
        (void)timer;
        PanelController *owner = weakSelf;
        if (owner.optionHeld && (NSEvent.modifierFlags & mask) == NSEventModifierFlagOption) {
            NSString *selection = [owner selectedTextFromFrontmostApp];
            if (selection.length) [owner presentTranslationText:selection];
            else [owner copySelectionOrShowChat];
        }
        owner.optionTimer = nil;
    }];
    [NSRunLoop.mainRunLoop addTimer:self.optionTimer forMode:NSRunLoopCommonModes];
}
- (void)refreshGlobalShortcut {
    if (!AXIsProcessTrusted()) {
        if (self.globalKeyMonitor) [NSEvent removeMonitor:self.globalKeyMonitor];
        self.globalKeyMonitor = nil;
        return;
    }
    if (self.globalKeyMonitor) return;
    __weak PanelController *weakSelf = self;
    self.globalKeyMonitor = [NSEvent addGlobalMonitorForEventsMatchingMask:
        NSEventMaskFlagsChanged | NSEventMaskKeyDown | NSEventMaskLeftMouseDown |
        NSEventMaskRightMouseDown | NSEventMaskOtherMouseDown | NSEventMaskScrollWheel
        handler:^(NSEvent *event) { [weakSelf handleOptionEvent:event]; }];
}
- (void)requestShortcutPermission:(id)sender {
    (void)sender;
    if (AXIsProcessTrusted()) {
        [self refreshGlobalShortcut];
    } else {
        AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)@{(__bridge id)kAXTrustedCheckOptionPrompt:@YES});
        [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:@"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"]];
    }
}
- (void)applicationDidBecomeActive:(NSNotification *)notification {
    (void)notification;
    [self refreshGlobalShortcut];
}
- (void)applicationWillTerminate:(NSNotification *)notification {
    (void)notification;
    [self.optionTimer invalidate];
    [self.selectionTimer invalidate];
    if (self.globalKeyMonitor) [NSEvent removeMonitor:self.globalKeyMonitor];
    if (self.localKeyMonitor) [NSEvent removeMonitor:self.localKeyMonitor];
}
- (NSString *)selectedTextFromFrontmostApp {
    self.selectionIsSecure = NO;
    NSRunningApplication *application = NSWorkspace.sharedWorkspace.frontmostApplication;
    if (!AXIsProcessTrusted() || !application || application.processIdentifier == NSProcessInfo.processInfo.processIdentifier) return @"";
    AXUIElementRef app = AXUIElementCreateApplication(application.processIdentifier);
    AXUIElementSetMessagingTimeout(app, 0.25);
    CFTypeRef focused = NULL;
    CFTypeRef selected = NULL;
    CFTypeRef subrole = NULL;
    NSString *text = @"";
    if (AXUIElementCopyAttributeValue(app, kAXFocusedUIElementAttribute, &focused) == kAXErrorSuccess &&
        CFGetTypeID(focused) == AXUIElementGetTypeID()) {
        AXUIElementCopyAttributeValue((AXUIElementRef)focused, kAXSubroleAttribute, &subrole);
        BOOL secure = subrole && CFEqual(subrole, kAXSecureTextFieldSubrole);
        self.selectionIsSecure = secure;
        if (!secure && AXUIElementCopyAttributeValue((AXUIElementRef)focused, kAXSelectedTextAttribute, &selected) == kAXErrorSuccess &&
            CFGetTypeID(selected) == CFStringGetTypeID()) text = [(__bridge NSString *)selected copy];
        // 部分应用只暴露选区范围，通过参数化属性获取对应文字。
        if (!secure && !text.length) {
            CFTypeRef range = NULL;
            CFTypeRef value = NULL;
            if (AXUIElementCopyAttributeValue((AXUIElementRef)focused, kAXSelectedTextRangeAttribute, &range) == kAXErrorSuccess &&
                CFGetTypeID(range) == AXValueGetTypeID() && AXValueGetType((AXValueRef)range) == kAXValueCFRangeType) {
                CFRange selectedRange;
                if (AXValueGetValue((AXValueRef)range, kAXValueCFRangeType, &selectedRange) && selectedRange.length > 0 &&
                    AXUIElementCopyParameterizedAttributeValue((AXUIElementRef)focused, kAXStringForRangeParameterizedAttribute, range, &value) == kAXErrorSuccess &&
                    CFGetTypeID(value) == CFStringGetTypeID()) text = [(__bridge NSString *)value copy];
            }
            if (value) CFRelease(value);
            if (range) CFRelease(range);
        }
    }
    if (subrole) CFRelease(subrole);
    if (selected) CFRelease(selected);
    if (focused) CFRelease(focused);
    CFRelease(app);
    return text;
}
// 浏览器、微信等未提供 AX 选区时，通过应用自己的复制能力获取文本。
- (void)copySelectionOrShowChat {
    if (self.selectionTimer) return;
    NSRunningApplication *source = NSWorkspace.sharedWorkspace.frontmostApplication;
    if (self.selectionIsSecure || !AXIsProcessTrusted() || !source || source.processIdentifier == NSProcessInfo.processInfo.processIdentifier) {
        [self showWindow:nil];
        return;
    }
    __weak PanelController *weakSelf = self;
    NSDate *started = NSDate.date;
    __block NSDate *copiedAt = nil;
    __block NSInteger originalChange = 0;
    __block NSArray<NSPasteboardItem *> *savedItems = nil;
    self.selectionTimer = [NSTimer timerWithTimeInterval:0.05 repeats:YES block:^(NSTimer *timer) {
        PanelController *owner = weakSelf;
        if (!owner) { [timer invalidate]; return; }
        // 长按达到阈值后立即读取；切换应用则取消，避免复制错误窗口。
        if (NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier != source.processIdentifier || -started.timeIntervalSinceNow > 5) {
            [timer invalidate]; owner.selectionTimer = nil;
            return;
        }
        NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
        if (!copiedAt) {
            NSEventModifierFlags otherModifiers = NSEvent.modifierFlags & (NSEventModifierFlagCommand | NSEventModifierFlagControl | NSEventModifierFlagShift | NSEventModifierFlagFunction);
            if (otherModifiers) {
                [timer invalidate]; owner.selectionTimer = nil;
                return;
            }
            // 复制所有已发布的剪贴板格式，图片和富文本也一并保留。
            originalChange = pasteboard.changeCount;
            NSMutableArray *snapshot = [NSMutableArray array];
            for (NSPasteboardItem *item in pasteboard.pasteboardItems) {
                NSPasteboardItem *saved = [NSPasteboardItem new];
                for (NSPasteboardType type in item.types) {
                    NSData *data = [item dataForType:type];
                    if (data) [saved setData:data forType:type];
                }
                if (saved.types.count) [snapshot addObject:saved];
            }
            savedItems = snapshot;
            if (pasteboard.changeCount != originalChange || NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier != source.processIdentifier) {
                [timer invalidate]; owner.selectionTimer = nil;
                return;
            }
            // 使用独立按键状态并明确只设置 Command，不继承用户仍按住的 Option。
            CGEventSourceRef copySource = CGEventSourceCreate(kCGEventSourceStatePrivate);
            CGEventRef down = copySource ? CGEventCreateKeyboardEvent(copySource, 8, true) : NULL;
            CGEventRef up = copySource ? CGEventCreateKeyboardEvent(copySource, 8, false) : NULL;
            if (copySource) CFRelease(copySource);
            if (!down || !up) {
                if (down) CFRelease(down);
                if (up) CFRelease(up);
                [timer invalidate]; owner.selectionTimer = nil;
                [owner showWindow:nil];
                return;
            }
            CGEventSetFlags(down, kCGEventFlagMaskCommand);
            CGEventSetFlags(up, kCGEventFlagMaskCommand);
            CGEventPostToPid(source.processIdentifier, down);
            CGEventPostToPid(source.processIdentifier, up);
            CFRelease(down); CFRelease(up);
            copiedAt = NSDate.date;
            return;
        }
        if (pasteboard.changeCount == originalChange && -copiedAt.timeIntervalSinceNow < 0.8) return;
        NSString *selection = @"";
        if (pasteboard.changeCount != originalChange) {
            NSInteger copiedChange = pasteboard.changeCount;
            selection = [[pasteboard stringForType:NSPasteboardTypeString] copy] ?: @"";
            // 只恢复仍然是本次读取版本的剪贴板，避免覆盖随后发生的更新。
            if (pasteboard.changeCount == copiedChange) {
                [pasteboard clearContents];
                if (savedItems.count) [pasteboard writeObjects:savedItems];
            }
        }
        [timer invalidate]; owner.selectionTimer = nil;
        if (selection.length) [owner presentTranslationText:selection];
        else [owner showWindow:nil];
    }];
    [NSRunLoop.mainRunLoop addTimer:self.selectionTimer forMode:NSRunLoopCommonModes];
}
- (void)presentTranslationText:(NSString *)text {
    if (text.length) self.pendingTranslation = text;
    if (!self.translationPanel) {
        self.translationURL = [self.pageURL.URLByDeletingLastPathComponent URLByAppendingPathComponent:@"translation.html"];
        self.translationPanel = [[SynorPanel alloc] initWithContentRect:NSMakeRect(0, 0, 520, 660)
            styleMask:NSWindowStyleMaskBorderless | NSWindowStyleMaskNonactivatingPanel | NSWindowStyleMaskResizable
            backing:NSBackingStoreBuffered defer:NO];
        self.translationPanel.title = @"Synor · 快捷翻译";
        self.translationPanel.contentMinSize = NSMakeSize(420, 500);
        self.translationPanel.level = NSFloatingWindowLevel;
        self.translationPanel.opaque = NO;
        self.translationPanel.backgroundColor = NSColor.clearColor;
        self.translationPanel.hasShadow = YES;
        self.translationPanel.movableByWindowBackground = YES;
        self.translationPanel.hidesOnDeactivate = NO;
        self.translationPanel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorFullScreenAuxiliary;
        NSGlassEffectView *glass = [[NSGlassEffectView alloc] initWithFrame:NSMakeRect(0, 0, 520, 660)];
        glass.style = NSGlassEffectViewStyleRegular;
        glass.cornerRadius = 18;
        glass.clipsToBounds = YES;
        glass.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        NSView *content = [[NSView alloc] initWithFrame:glass.bounds];
        content.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        glass.contentView = content;
        self.translationPanel.contentView = glass;
        NSURL *iconURL = [[self.pageURL.URLByDeletingLastPathComponent URLByDeletingLastPathComponent]
            URLByAppendingPathComponent:@"Assets/MenuBarIcon.png"];
        NSImageView *logo = [[NSImageView alloc] initWithFrame:NSMakeRect(28, 625, 24, 24)];
        logo.image = [[NSImage alloc] initWithContentsOfURL:iconURL];
        logo.imageScaling = NSImageScaleProportionallyUpOrDown;
        logo.autoresizingMask = NSViewMinYMargin;
        logo.accessibilityLabel = @"Synor 品牌标志";
        [content addSubview:logo];
        NSTextField *title = [NSTextField labelWithString:@"SYNOR · 翻译"];
        title.font = [NSFont systemFontOfSize:11 weight:NSFontWeightMedium];
        title.textColor = NSColor.secondaryLabelColor;
        title.frame = NSMakeRect(60, 628, 300, 18);
        title.autoresizingMask = NSViewWidthSizable | NSViewMinYMargin;
        [content addSubview:title];
        NSTextField *escapeHint = [NSTextField labelWithString:@"Esc 隐藏"];
        escapeHint.font = [NSFont systemFontOfSize:11];
        escapeHint.textColor = NSColor.secondaryLabelColor;
        escapeHint.alignment = NSTextAlignmentRight;
        escapeHint.frame = NSMakeRect(420, 628, 72, 18);
        escapeHint.autoresizingMask = NSViewMinXMargin | NSViewMinYMargin;
        [content addSubview:escapeHint];
        WKWebViewConfiguration *configuration = [WKWebViewConfiguration new];
        configuration.websiteDataStore = WKWebsiteDataStore.nonPersistentDataStore;
        [configuration.userContentController addScriptMessageHandler:self name:@"synor"];
        WKWebView *web = [[WKWebView alloc] initWithFrame:NSMakeRect(16, 16, 488, 604) configuration:configuration];
        web.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        web.navigationDelegate = self;
        [web setValue:@NO forKey:@"drawsBackground"];
        web.underPageBackgroundColor = NSColor.clearColor;
        self.translationPanel.chatView = web;
        [content addSubview:web];
        [web loadFileURL:self.translationURL allowingReadAccessToURL:self.translationURL.URLByDeletingLastPathComponent];
        [self.translationPanel center];
    }
    [NSApp activateIgnoringOtherApps:YES];
    [self.translationPanel makeKeyAndOrderFront:nil];
    [self deliverTranslation];
}
- (void)deliverTranslation {
    if (!self.translationReady || !self.pendingTranslation) return;
    [self.translationPanel.chatView callAsyncJavaScript:@"window.dispatchEvent(new CustomEvent('synor-chat', {detail:{type:'open-translation',text:text}}));"
        arguments:@{@"text":self.pendingTranslation} inFrame:nil inContentWorld:WKContentWorld.pageWorld completionHandler:nil];
    self.pendingTranslation = nil;
}
- (void)showTranslation:(id)sender {
    (void)sender;
    [self presentTranslationText:[self selectedTextFromFrontmostApp]];
}
- (void)showWindow:(id)sender {
    (void)sender;
    [NSApp activateIgnoringOtherApps:YES];
    [self.panel makeKeyAndOrderFront:nil];
}
- (void)hideWindow:(id)sender {
    (void)sender;
    [self.panel orderOut:nil];
}
- (void)statusClicked:(id)sender {
    (void)sender;
    if (NSApp.currentEvent.type == NSEventTypeRightMouseUp) {
        [NSMenu popUpContextMenu:self.statusMenu withEvent:NSApp.currentEvent forView:self.statusItem.button];
    } else {
        [self showWindow:nil];
    }
}
- (BOOL)applicationShouldHandleReopen:(NSApplication *)application hasVisibleWindows:(BOOL)visible {
    (void)application; (void)visible;
    [self showWindow:nil];
    return YES;
}
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)application {
    (void)application;
    return NO;
}
- (void)webView:(WKWebView *)webView decidePolicyForNavigationAction:(WKNavigationAction *)action
    decisionHandler:(void (^)(WKNavigationActionPolicy))decisionHandler {
    NSURL *allowedURL = webView == self.translationPanel.chatView ? self.translationURL : self.pageURL;
    // 每个窗口只允许加载自己的本地页面。
    decisionHandler([action.request.URL isEqual:allowedURL] ? WKNavigationActionPolicyAllow : WKNavigationActionPolicyCancel);
}
- (void)userContentController:(WKUserContentController *)controller didReceiveScriptMessage:(WKScriptMessage *)message {
    (void)controller;
    NSURL *allowedURL = message.webView == self.translationPanel.chatView ? self.translationURL : self.pageURL;
    if (!message.frameInfo.isMainFrame || ![message.frameInfo.request.URL isEqual:allowedURL] ||
        ![message.body isKindOfClass:NSDictionary.class]) return;
    NSDictionary *body = message.body;
    if ([body[@"type"] isEqual:@"open-translation"]) [self showTranslation:nil];
    if (([body[@"type"] isEqual:@"chat"] || [body[@"type"] isEqual:@"translate"] || [body[@"type"] isEqual:@"cancel"] || [body[@"type"] isEqual:@"settings-load"] || [body[@"type"] isEqual:@"settings-save"]) && chatRequest) {
        if (![body[@"type"] isEqual:@"cancel"] && [body[@"id"] isKindOfClass:NSString.class]) {
            [requestViews setObject:message.webView forKey:body[@"id"]];
        }
        NSData *data = [NSJSONSerialization dataWithJSONObject:body options:0 error:nil];
        if (data) chatRequest([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String);
    }
    if ([body[@"type"] isEqual:@"ready"]) {
        if (message.webView == self.translationPanel.chatView) { self.translationReady = YES; [self deliverTranslation]; }
        NSLog(@"Synor React chat mounted successfully");
    }
    if ([body[@"type"] isEqual:@"close"]) {
        if (message.webView == self.translationPanel.chatView) [self.translationPanel orderOut:nil];
        else [self hideWindow:nil];
    }
    if ([body[@"type"] isEqual:@"paste"]) {
        NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
        NSImage *image = [[NSImage alloc] initWithPasteboard:pasteboard];
        NSDictionary *payload;
        if (image) {
            NSBitmapImageRep *bitmap = [NSBitmapImageRep imageRepWithData:image.TIFFRepresentation];
            NSData *png = [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
            payload = png ? @{@"image":[png base64EncodedStringWithOptions:0]} : @{@"error":@"无法读取剪贴板图片，请重新复制图片后再试。"};
        } else {
            payload = @{@"text":[pasteboard stringForType:NSPasteboardTypeString] ?: @""};
        }
        [message.webView callAsyncJavaScript:@"window.dispatchEvent(new CustomEvent('synor-paste', {detail: payload}));"
            arguments:@{@"payload":payload} inFrame:nil inContentWorld:WKContentWorld.pageWorld
            completionHandler:^(id result, NSError *error) {
                (void)result;
                if (error) NSLog(@"Native paste delivery failed: %@", error.localizedDescription);
            }];
    }
    if ([body[@"type"] isEqual:@"copy"] && [body[@"text"] isKindOfClass:NSString.class]) {
        [NSPasteboard.generalPasteboard clearContents];
        [NSPasteboard.generalPasteboard setString:body[@"text"] forType:NSPasteboardTypeString];
    }
}
@end

int synor_panel_run(const char *pagePath, void (*callback)(const char *)) {
    chatRequest = callback;
    requestViews = [NSMapTable strongToWeakObjectsMapTable];
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        PanelController *controller = [PanelController new];
        controller.pageURL = [NSURL fileURLWithPath:[NSString stringWithUTF8String:pagePath]];
        NSApp.delegate = controller;
        [NSApp run];
        (void)controller;
    }
    return 0;
}
