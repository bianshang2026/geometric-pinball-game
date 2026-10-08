// WebViewPlugin.mm
// WKWebView plugin for Unity/Tuanjie Editor
// Minimal implementation for embedding WebKit browser in Unity EditorWindow

#import <Foundation/Foundation.h>
#import <AppKit/AppKit.h>
#import <WebKit/WebKit.h>
#import <objc/runtime.h>
#include <string>

// Forward declarations
static NSView* SearchViewHierarchy(NSView* view, void* targetGUIViewPtr);

// Navigation delegate to monitor loading
@interface WebViewNavigationDelegate : NSObject <WKNavigationDelegate>
@property (nonatomic, assign) BOOL hasLoadFailure;
@end

@implementation WebViewNavigationDelegate

- (void)webView:(WKWebView *)webView didFailNavigation:(WKNavigation *)navigation withError:(NSError *)error {
    NSLog(@"WebViewPlugin: Navigation failed: %@", error.localizedDescription);
    self.hasLoadFailure = YES;
}

- (void)webView:(WKWebView *)webView didFailProvisionalNavigation:(WKNavigation *)navigation withError:(NSError *)error {
    NSLog(@"WebViewPlugin: Provisional navigation failed: %@", error.localizedDescription);
    self.hasLoadFailure = YES;
}

- (void)webView:(WKWebView *)webView didFinishNavigation:(WKNavigation *)navigation {
    // Reset failure flag on successful load
    self.hasLoadFailure = NO;
}

@end

// Custom WKWebView subclass to support drag and drop and dev tools
@interface DragSupportWebView : WKWebView <WKUIDelegate>
@property (nonatomic, strong) id inspector; // Store inspector reference
@property (nonatomic, strong) WebViewNavigationDelegate* navDelegate; // Store navigation delegate
@property (nonatomic, strong) NSTimer* dragKeepAliveTimer;
@property (nonatomic, strong) id keyEventMonitor;
@property (nonatomic, strong) id mouseEventMonitor;
@property (nonatomic, assign) BOOL commandKeyCaptureEnabled;
@end

@implementation DragSupportWebView

- (BOOL)acceptsFirstResponder {
    return YES;
}

- (BOOL)focusForEditingCommands {
    NSWindow* window = self.window;
    if (!window) {
        return NO;
    }

    if (![NSApp isActive]) {
        [NSApp activateIgnoringOtherApps:YES];
    }

    [window makeKeyAndOrderFront:nil];
    if (window.firstResponder != self) {
        [window makeFirstResponder:self];
    }

    self.commandKeyCaptureEnabled = (window.firstResponder == self);
    return self.commandKeyCaptureEnabled;
}

- (instancetype)initWithFrame:(NSRect)frameRect configuration:(WKWebViewConfiguration *)configuration {
    self = [super initWithFrame:frameRect configuration:configuration];
    if (self) {
        [self registerForFileURLDragTypes];
    }
    return self;
}

- (void)registerForFileURLDragTypes {
    NSMutableArray* types = [NSMutableArray arrayWithArray:[self registeredDraggedTypes]];
    NSArray* fileTypes = @[NSPasteboardTypeFileURL, @"NSFilenamesPboardType", NSPasteboardTypeURL];
    for (NSString* ft in fileTypes) {
        if (![types containsObject:ft]) {
            [types addObject:ft];
        }
    }
    [self registerForDraggedTypes:types];
}

- (void)viewDidMoveToSuperview {
    [super viewDidMoveToSuperview];
    NSLog(@"WebViewPlugin: viewDidMoveToSuperview called, superview: %@", self.superview);
}

- (void)viewDidMoveToWindow {
    [super viewDidMoveToWindow];

    if (self.window != nil) {
        [self installKeyEventMonitorIfNeeded];
        [self installMouseEventMonitorIfNeeded];
    } else {
        [self removeKeyEventMonitor];
        [self removeMouseEventMonitor];
    }
}

- (void)mouseDown:(NSEvent *)event {
    [self focusForEditingCommands];
    [super mouseDown:event];
}

- (void)rightMouseDown:(NSEvent *)event {
    [self focusForEditingCommands];
    [super rightMouseDown:event];
}

- (BOOL)sendEditingAction:(SEL)action {
    [self focusForEditingCommands];

    if ([NSApp sendAction:action to:nil from:self]) {
        return YES;
    }

    if ([self respondsToSelector:action]) {
        return [self tryToPerform:action with:nil];
    }

    return NO;
}

- (BOOL)isSupportedCommandKeyEvent:(NSEvent *)event {
    if (event.type != NSEventTypeKeyDown) {
        return NO;
    }

    NSEventModifierFlags flags = (event.modifierFlags & NSEventModifierFlagDeviceIndependentFlagsMask);
    BOOL hasCommand = (flags & NSEventModifierFlagCommand) == NSEventModifierFlagCommand;
    BOOL hasNoExtraModifiers = (flags & ~NSEventModifierFlagCommand) == 0;

    if (!hasCommand) {
        return NO;
    }

    NSString* chars = event.charactersIgnoringModifiers.lowercaseString;
    return hasNoExtraModifiers && [chars isEqualToString:@"v"];
}

- (BOOL)containsWindowPoint:(NSPoint)windowPoint {
    if (self.window == nil || self.isHidden || self.superview == nil) {
        return NO;
    }

    NSPoint localPoint = [self convertPoint:windowPoint fromView:nil];
    return NSPointInRect(localPoint, self.bounds);
}

- (void)updateCommandKeyCaptureForMouseEvent:(NSEvent *)event {
    NSWindow* window = self.window;
    if (window == nil || event.windowNumber != window.windowNumber) {
        return;
    }

    BOOL shouldCapture = [self containsWindowPoint:event.locationInWindow];
    self.commandKeyCaptureEnabled = shouldCapture;
}

- (NSEvent *)handleLocalKeyEvent:(NSEvent *)event {
    NSWindow* window = self.window;
    if (window == nil || event.windowNumber != window.windowNumber) {
        return event;
    }

    if (!self.commandKeyCaptureEnabled) {
        return event;
    }

    if (![self isSupportedCommandKeyEvent:event]) {
        return event;
    }

    if ([[event.charactersIgnoringModifiers lowercaseString] isEqualToString:@"v"]) {
        if ([self sendEditingAction:@selector(paste:)]) {
            return nil;
        }
    }

    return event;
}

- (void)installKeyEventMonitorIfNeeded {
    if (self.keyEventMonitor != nil) {
        return;
    }

    DragSupportWebView* __weak weakSelf = self;
    self.keyEventMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskKeyDown handler:^NSEvent * _Nullable(NSEvent * _Nonnull event) {
        DragSupportWebView* strongSelf = weakSelf;
        if (strongSelf == nil) {
            return event;
        }

        return [strongSelf handleLocalKeyEvent:event];
    }];

}

- (void)installMouseEventMonitorIfNeeded {
    if (self.mouseEventMonitor != nil) {
        return;
    }

    DragSupportWebView* __weak weakSelf = self;
    NSEventMask mouseMask = NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown | NSEventMaskOtherMouseDown;
    self.mouseEventMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:mouseMask handler:^NSEvent * _Nullable(NSEvent * _Nonnull event) {
        DragSupportWebView* strongSelf = weakSelf;
        if (strongSelf == nil) {
            return event;
        }

        [strongSelf updateCommandKeyCaptureForMouseEvent:event];
        return event;
    }];
}

- (void)removeKeyEventMonitor {
    if (self.keyEventMonitor == nil) {
        return;
    }

    [NSEvent removeMonitor:self.keyEventMonitor];
    self.keyEventMonitor = nil;
}

- (void)removeMouseEventMonitor {
    if (self.mouseEventMonitor == nil) {
        return;
    }

    [NSEvent removeMonitor:self.mouseEventMonitor];
    self.mouseEventMonitor = nil;
    self.commandKeyCaptureEnabled = NO;
}

// Show Web Inspector using private API
- (void)showInspector:(id)sender {
    @try {
        // Use KVC to access _inspector (private API but commonly used)
        id inspector = [self valueForKey:@"_inspector"];
        if (inspector) {
            // Show the inspector window
            [inspector performSelector:@selector(show)];
            self.inspector = inspector; // Keep reference
            NSLog(@"WebViewPlugin: Web Inspector opened");
        } else {
            NSLog(@"WebViewPlugin: Inspector not available");
        }
    }
    @catch (NSException *exception) {
        NSLog(@"WebViewPlugin: Failed to open inspector: %@", exception.reason);
    }
}

// MARK: - WKUIDelegate: native file open panel
//
// WKWebView never shows a file picker for <input type="file"> on its own:
// the embedding app must implement this delegate method, otherwise the click
// is a silent no-op (e.g. the "add reference image" button of the embedded AI
// generation page). Windows (WebView2) opens the OS dialog automatically,
// so this gap only surfaces on macOS. Works for the main frame and nested
// iframes (the frame is passed via initiatedByFrame).

- (void)webView:(WKWebView *)webView
    runOpenPanelWithParameters:(WKOpenPanelParameters *)parameters
    initiatedByFrame:(WKFrameInfo *)frame
    completionHandler:(void (^)(NSArray<NSURL *> * _Nullable URLs))completionHandler
{
    NSOpenPanel* panel = [NSOpenPanel openPanel];
    panel.canChooseFiles = YES;
    panel.canChooseDirectories = NO;
    panel.resolvesAliases = YES;
    panel.allowsMultipleSelection = parameters.allowsMultipleSelection;

    if ([panel runModal] == NSModalResponseOK) {
        completionHandler(panel.URLs);
    } else {
        completionHandler(nil);
    }
}

// MARK: - Keep JS alive during external drag sessions
//
// When files are dragged from Finder into the Unity window, macOS enters
// NSEventTrackingRunLoopMode. WKWebView's JS display-link and IPC are
// scheduled on NSDefaultRunLoopMode only, so dynamic content (timers,
// requestAnimationFrame, clock UIs) stalls while CSS animations (Core
// Animation / GPU) keep running. We work around this by briefly pumping
// NSDefaultRunLoopMode from a timer registered on NSRunLoopCommonModes.

- (NSDragOperation)draggingEntered:(id<NSDraggingInfo>)sender {
    if (![NSApp isActive]) {
        [NSApp activateIgnoringOtherApps:YES];
    }
    [self startDragKeepAliveTimer];
    return [super draggingEntered:sender];
}

- (void)draggingExited:(id<NSDraggingInfo>)sender {
    [self stopDragKeepAliveTimer];
    [super draggingExited:sender];
}

- (void)draggingEnded:(id<NSDraggingInfo>)sender {
    [self stopDragKeepAliveTimer];
    [super draggingEnded:sender];
}

- (BOOL)performDragOperation:(id<NSDraggingInfo>)sender {
    [self stopDragKeepAliveTimer];
    return [super performDragOperation:sender];
}

- (void)startDragKeepAliveTimer {
    if (self.dragKeepAliveTimer) return;
    self.dragKeepAliveTimer = [NSTimer timerWithTimeInterval:1.0/60.0
                                                     target:self
                                                   selector:@selector(dragKeepAliveTick:)
                                                   userInfo:nil
                                                    repeats:YES];
    [[NSRunLoop currentRunLoop] addTimer:self.dragKeepAliveTimer forMode:NSRunLoopCommonModes];
}

- (void)stopDragKeepAliveTimer {
    [self.dragKeepAliveTimer invalidate];
    self.dragKeepAliveTimer = nil;
}

- (void)dragKeepAliveTick:(NSTimer*)timer {
    [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode
                             beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.001]];
}

- (void)removeFromSuperview {
    [self removeKeyEventMonitor];
    [self removeMouseEventMonitor];
    [self stopDragKeepAliveTimer];
    [super removeFromSuperview];
}

@end

// Keep a static reference to the navigation delegate
static WebViewNavigationDelegate* s_navigationDelegate = nil;

// Global registry to track valid WebView handles
// This allows safe validation of handles across domain reloads
static NSMutableSet<NSValue*>* s_validWebViewHandles = nil;

// Initialize the registry on first use
static void EnsureRegistryInitialized() {
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        s_validWebViewHandles = [[NSMutableSet alloc] init];
    });
}

// Register a WebView handle as valid
static void RegisterWebViewHandle(void* handle) {
    if (handle == nullptr) return;
    EnsureRegistryInitialized();
    @synchronized(s_validWebViewHandles) {
        [s_validWebViewHandles addObject:[NSValue valueWithPointer:handle]];
    }
}

// Unregister a WebView handle
static void UnregisterWebViewHandle(void* handle) {
    if (handle == nullptr) return;
    EnsureRegistryInitialized();
    @synchronized(s_validWebViewHandles) {
        [s_validWebViewHandles removeObject:[NSValue valueWithPointer:handle]];
    }
}

// Check if a handle is registered
static bool IsWebViewHandleRegistered(void* handle) {
    if (handle == nullptr) return false;
    EnsureRegistryInitialized();
    @synchronized(s_validWebViewHandles) {
        return [s_validWebViewHandles containsObject:[NSValue valueWithPointer:handle]];
    }
}

extern "C" {
    void* WebView_Create(void* guiViewPtr, const char* url, float x, float y, float w, float h);
    void* WebView_CreateByWindowTitle(const char* windowTitle, const char* url, float x, float y, float w, float h);
    void WebView_Destroy(void* handle);
    void WebView_UpdateFrame(void* handle, float x, float y, float w, float h);
    void WebView_UpdateParentAndFrame(void* handle, void* guiViewPtr, float x, float y, float w, float h);
    void WebView_LoadURL(void* handle, const char* url);
    void WebView_GoBack(void* handle);
    void WebView_GoForward(void* handle);
    void WebView_Reload(void* handle);
    void WebView_ExecuteJavaScript(void* handle, const char* script);
    void WebView_SetHidden(void* handle, bool hidden);
    void WebView_ShowInspector(void* handle);
    void WebView_Focus(void* handle);
    bool WebView_ValidateHandle(void* handle);
    bool WebView_HasValidContent(void* handle);
    bool Codely_IsMouseInNSWindowMatchingTitle(const char* windowTitle);
}

// Helper to find NSView from window title
NSView* FindNSViewByWindowTitle(const char* windowTitle) {
    @autoreleasepool {
        if (windowTitle == nullptr || strlen(windowTitle) == 0) {
            NSLog(@"WebViewPlugin: ERROR - No window title provided");
            return nil;
        }
        
        NSString* targetTitle = [NSString stringWithUTF8String:windowTitle];
        
        // Get all windows
        NSArray<NSWindow*>* windows = [NSApp windows];
        
        // Search for window by title
        for (NSWindow* window in windows) {
            NSString* title = [window title];
            
            if ([title isEqualToString:targetTitle]) {
                NSView* contentView = [window contentView];
                
                // Find the deepest GUIRenderView or similar
                NSMutableArray* stack = [NSMutableArray arrayWithObject:contentView];
                NSView* bestCandidate = contentView;
                
                while ([stack count] > 0) {
                    NSView* view = [stack lastObject];
                    [stack removeLastObject];
                    
                    NSString* className = NSStringFromClass([view class]);
                    
                    // Prefer GUIRenderView if found
                    if ([className containsString:@"GUIRenderView"]) {
                        return view;
                    }
                    
                    // Track the best candidate (deepest view with subviews)
                    if ([[view subviews] count] > 0) {
                        bestCandidate = view;
                    }
                    
                    // Add subviews to stack
                    [stack addObjectsFromArray:[view subviews]];
                }
                
                return bestCandidate;
            }
        }
        
        NSLog(@"WebViewPlugin: ❌ ERROR - No window found with title: %@", targetTitle);
        return nil;
    }
}

// Helper: Recursively search view hierarchy for NSView with matching m_View pointer
static NSView* SearchViewHierarchy(NSView* view, void* targetGUIViewPtr) {
    if (!view) return nil;
    
    @try {
        NSString* className = view.className;
        
        // Check if this is a GUI-related view
        if ([className hasPrefix:@"GUI"]) {
            Class viewClass = [view class];
            Ivar ivar = class_getInstanceVariable(viewClass, "m_View");
            
            if (ivar) {
                // Get the m_View pointer from this NSView
                void* m_View = *(void**)((char*)(__bridge void*)view + ivar_getOffset(ivar));
                
                // Check if this matches our target GUIView pointer
                if (m_View == targetGUIViewPtr) {
                    // IMPORTANT: Verify that this NSView is still attached to a window
                    // During window transitions (dock/undock), NSView may exist but be detached
                    NSWindow* viewWindow = [view window];
                    if (viewWindow == nil) {
                        return nil;  // Skip this detached view
                    }
                    
                    return view;
                }
            }
        }
        
        // Recursively search subviews
        for (NSView* subview in view.subviews) {
            NSView* result = SearchViewHierarchy(subview, targetGUIViewPtr);
            if (result) {
                return result;
            }
        }
    }
    @catch (NSException *e) {
        return nil;
    }
    
    return nil;
}

// Convert GUIView pointer (C++) to NSView pointer (GUIRenderView/NSView)
// The guiViewPtr is actually a pointer to the C++ GUIView object, not an NSView
// We need to find the NSView that wraps this GUIView by searching for m_View member
NSView* FindNSViewFromGUIView(void* guiViewPtr) {
    @autoreleasepool {
        if (guiViewPtr == nullptr) {
            return nil;
        }
        
        // Get all windows
        NSArray<NSWindow*>* windows = [NSApp windows];
        
        // Strategy: Search floating/separate windows first, then main window
        // This ensures we find the correct window during dock/undock transitions
        
        NSMutableArray* floatingWindows = [NSMutableArray array];
        NSMutableArray* otherWindows = [NSMutableArray array];
        
        for (NSWindow* window in windows) {
            // Separate floating windows from main window
            // Floating Codely windows typically have smaller size and different position
            NSRect frame = [window frame];
            BOOL isMainEditor = [window isMainWindow] && frame.size.width > 1000;  // Main editor is usually large
            
            if (isMainEditor) {
                [otherWindows addObject:window];
            } else {
                [floatingWindows addObject:window];  // Search these first
            }
        }
        
        // Search floating windows first
        for (NSWindow* window in floatingWindows) {
            NSView* contentView = window.contentView;
            if (!contentView) continue;
            
            NSView* result = SearchViewHierarchy(contentView, guiViewPtr);
            if (result) {
                return result;
            }
        }
        
        // Search other windows (including main editor)
        for (NSWindow* window in otherWindows) {
            NSView* contentView = window.contentView;
            if (!contentView) continue;
            
            NSView* result = SearchViewHierarchy(contentView, guiViewPtr);
            if (result) {
                return result;
            }
        }
        
        return nil;
    }
}

// Create WKWebView and embed it
void* WebView_Create(void* guiViewPtr, const char* url, float x, float y, float w, float h) {
    @autoreleasepool {
        NSView* parentView = FindNSViewFromGUIView(guiViewPtr);
        if (parentView == nil) {
            return nullptr;
        }
        
        NSRect frame = NSMakeRect(x, y, w, h);
        
        WKWebViewConfiguration* config = [[WKWebViewConfiguration alloc] init];
        
        // Enable developer extras for debugging
        [config.preferences setValue:@YES forKey:@"developerExtrasEnabled"];
        
        // Enable additional debugging features (private APIs but commonly used)
        @try {
            [config.preferences setValue:@YES forKey:@"allowsInlineMediaPlayback"];
            [config.preferences setValue:@YES forKey:@"javaScriptCanOpenWindowsAutomatically"];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Warning setting preferences: %@", exception.reason);
        }
        
        // Disable transparent background
        config.suppressesIncrementalRendering = NO;
        
        DragSupportWebView* webView = [[DragSupportWebView alloc] initWithFrame:frame configuration:config];
        
        NSLog(@"WebViewPlugin: Created DragSupportWebView at frame: %@", NSStringFromRect(frame));
        
        // Set navigation delegate for monitoring
        if (s_navigationDelegate == nil) {
            s_navigationDelegate = [[WebViewNavigationDelegate alloc] init];
        }
        [webView setNavigationDelegate:s_navigationDelegate];
        webView.navDelegate = s_navigationDelegate; // Store reference for status checking

        // Self as UI delegate: enables <input type="file"> open panel on macOS
        [webView setUIDelegate:webView];

        // Configure webView for proper display
        [webView setWantsLayer:YES];
        
        // Set opaque background
        [[webView layer] setOpaque:YES];
        [[webView layer] setBackgroundColor:[[NSColor whiteColor] CGColor]];
        
        // Ensure it's visible and on top
        [[webView layer] setZPosition:1000];
        [webView setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];
        [webView setHidden:NO];
        
        NSLog(@"WebViewPlugin: Adding webView to parent view: %@", parentView);
        
        // Add to parent view
        [parentView addSubview:webView];
        
        NSLog(@"WebViewPlugin: WebView added to superview, registeredDraggedTypes: %@", [webView registeredDraggedTypes]);
        
        // Make sure it's ordered to front
        [webView.superview setNeedsDisplay:YES];
        
        // Load URL if provided
        if (url != nullptr && strlen(url) > 0) {
            NSString* urlString = [NSString stringWithUTF8String:url];
            NSURL* nsUrl = [NSURL URLWithString:urlString];
            if (nsUrl != nil) {
                NSURLRequest* request = [NSURLRequest requestWithURL:nsUrl];
                [webView loadRequest:request];
            }
        }
        
        // Return retained pointer and register it
        void* handle = (__bridge_retained void*)webView;
        RegisterWebViewHandle(handle);
        return handle;
    }
}

// Create WKWebView by window title (preferred method)
void* WebView_CreateByWindowTitle(const char* windowTitle, const char* url, float x, float y, float w, float h) {
    @autoreleasepool {
        NSView* parentView = FindNSViewByWindowTitle(windowTitle);
        if (parentView == nil) {
            NSLog(@"WebViewPlugin: Failed to find parent NSView for window: %s", windowTitle);
            return nullptr;
        }
        
        // Create WKWebView
        NSRect frame = NSMakeRect(x, y, w, h);
        WKWebViewConfiguration* config = [[WKWebViewConfiguration alloc] init];
        
        // Enable developer extras for debugging
        [config.preferences setValue:@YES forKey:@"developerExtrasEnabled"];
        
        // Enable additional debugging features (private APIs but commonly used)
        @try {
            [config.preferences setValue:@YES forKey:@"allowsInlineMediaPlayback"];
            [config.preferences setValue:@YES forKey:@"javaScriptCanOpenWindowsAutomatically"];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Warning setting preferences: %@", exception.reason);
        }
        
        DragSupportWebView* webView = [[DragSupportWebView alloc] initWithFrame:frame configuration:config];
        
        // Set navigation delegate for monitoring
        if (s_navigationDelegate == nil) {
            s_navigationDelegate = [[WebViewNavigationDelegate alloc] init];
        }
        [webView setNavigationDelegate:s_navigationDelegate];
        webView.navDelegate = s_navigationDelegate; // Store reference for status checking

        // Self as UI delegate: enables <input type="file"> open panel on macOS
        [webView setUIDelegate:webView];

        // Configure webView
        [webView setWantsLayer:YES];
        [[webView layer] setZPosition:1000]; // Ensure it's on top
        [webView setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];
        
        // Add to parent view
        [parentView addSubview:webView];
        
        // Load URL if provided
        if (url != nullptr && strlen(url) > 0) {
            NSString* urlString = [NSString stringWithUTF8String:url];
            NSURL* nsUrl = [NSURL URLWithString:urlString];
            if (nsUrl != nil) {
                NSURLRequest* request = [NSURLRequest requestWithURL:nsUrl];
                [webView loadRequest:request];
            }
        }
        
        // Return retained pointer and register it
        void* handle = (__bridge_retained void*)webView;
        RegisterWebViewHandle(handle);
        return handle;
    }
}

// Destroy WKWebView
void WebView_Destroy(void* handle) {
    @autoreleasepool {
        if (handle == nullptr) return;
        
        // Unregister first (before potential deallocation)
        UnregisterWebViewHandle(handle);
        
        @try {
            WKWebView* webView = (__bridge_transfer WKWebView*)handle;
            
            // Stop loading
            [webView stopLoading];
            
            // Remove from superview
            [webView removeFromSuperview];
            
            // ARC will handle deallocation
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_Destroy: %@", exception.reason);
        }
    }
}

// Update frame
void WebView_UpdateFrame(void* handle, float x, float y, float w, float h) {
    @autoreleasepool {
        if (handle == nullptr) return;
        
        @try {
            WKWebView* webView = (__bridge WKWebView*)handle;
            NSRect newFrame = NSMakeRect(x, y, w, h);
            [webView setFrame:newFrame];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_UpdateFrame: %@", exception.reason);
        }
    }
}

// Update parent view and frame (when window/tab changes)
void WebView_UpdateParentAndFrame(void* handle, void* guiViewPtr, float x, float y, float w, float h) {
    @autoreleasepool {
        if (handle == nullptr) return;
        
        @try {
            WKWebView* webView = (__bridge WKWebView*)handle;
            
            // Get current parent
            NSView* currentParent = [webView superview];
            
            // Find the new parent view from guiViewPtr
            NSView* newParent = FindNSViewFromGUIView(guiViewPtr);
            if (newParent == nil) {
                return;
            }
            
            // Check if parent changed
            if (currentParent != newParent) {
                // Remove from current parent
                [webView removeFromSuperview];
                
                // Add to new parent
                [newParent addSubview:webView];
            }
            
            // Update frame
            NSRect newFrame = NSMakeRect(x, y, w, h);
            [webView setFrame:newFrame];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_UpdateParentAndFrame: %@", exception.reason);
        }
    }
}

// Load URL
void WebView_LoadURL(void* handle, const char* url) {
    @autoreleasepool {
        if (handle == nullptr || url == nullptr) return;
        
        WKWebView* webView = (__bridge WKWebView*)handle;
        NSString* urlString = [NSString stringWithUTF8String:url];
        NSURL* nsUrl = [NSURL URLWithString:urlString];
        
        if (nsUrl != nil) {
            NSURLRequest* request = [NSURLRequest requestWithURL:nsUrl];
            [webView loadRequest:request];
        }
    }
}

// Go back
void WebView_GoBack(void* handle) {
    @autoreleasepool {
        if (handle == nullptr) return;
        WKWebView* webView = (__bridge WKWebView*)handle;
        if ([webView canGoBack]) {
            [webView goBack];
        }
    }
}

// Go forward
void WebView_GoForward(void* handle) {
    @autoreleasepool {
        if (handle == nullptr) return;
        WKWebView* webView = (__bridge WKWebView*)handle;
        if ([webView canGoForward]) {
            [webView goForward];
        }
    }
}

// Reload
void WebView_Reload(void* handle) {
    @autoreleasepool {
        if (handle == nullptr) return;
        WKWebView* webView = (__bridge WKWebView*)handle;
        [webView reload];
    }
}

// Execute JavaScript
void WebView_ExecuteJavaScript(void* handle, const char* script) {
    @autoreleasepool {
        if (handle == nullptr || script == nullptr) {
            NSLog(@"WebViewPlugin: ExecuteJavaScript - NULL handle or script");
            return;
        }
        
        @try {
            WKWebView* webView = (__bridge WKWebView*)handle;
            NSString* scriptString = [NSString stringWithUTF8String:script];
            
            NSLog(@"WebViewPlugin: ExecuteJavaScript - About to execute script (length: %lu)", (unsigned long)[scriptString length]);
            NSLog(@"WebViewPlugin: ExecuteJavaScript - Script preview: %@", [scriptString length] > 200 ? [[scriptString substringToIndex:200] stringByAppendingString:@"..."] : scriptString);
            NSLog(@"WebViewPlugin: ExecuteJavaScript - WebView URL: %@", webView.URL);
            NSLog(@"WebViewPlugin: ExecuteJavaScript - WebView isLoading: %d", webView.isLoading);
            
            // Wrap the script to ensure it executes in page context and add error handling
            NSString* wrappedScript = [NSString stringWithFormat:@"(function() { try { %@ } catch(e) { console.error('Script error:', e); throw e; } })()", scriptString];
            
            [webView evaluateJavaScript:wrappedScript completionHandler:^(id result, NSError *error) {
                if (error != nil) {
                    NSLog(@"WebViewPlugin: JavaScript error: %@", error.localizedDescription);
                    NSLog(@"WebViewPlugin: JavaScript error code: %ld", (long)error.code);
                    NSLog(@"WebViewPlugin: JavaScript error domain: %@", error.domain);
                    NSLog(@"WebViewPlugin: JavaScript error userInfo: %@", error.userInfo);
                } else {
                    NSLog(@"WebViewPlugin: JavaScript executed successfully, result: %@", result);
                }
            }];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_ExecuteJavaScript: %@", exception.reason);
        }
    }
}

// True when the OS cursor lies inside an NSWindow whose trimmed title equals windowTitle (UTF-8).
// Matches Unity EditorWindow tab title vs. NSEvent.mouseLocation in screen coordinates.
bool Codely_IsMouseInNSWindowMatchingTitle(const char* windowTitle) {
    @autoreleasepool {
        if (windowTitle == nullptr) return false;
        NSString* wanted = [[NSString stringWithUTF8String:windowTitle]
            stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceAndNewlineCharacterSet]];
        if ([wanted length] == 0) return false;

        NSPoint p = [NSEvent mouseLocation];
        for (NSWindow* w in [NSApp windows]) {
            if (!w) continue;
            NSString* wt = [[w title] stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceAndNewlineCharacterSet]];
            if (![wt isEqualToString:wanted]) continue;
            if (NSPointInRect(p, w.frame)) return true;
        }
        return false;
    }
}

// Set WebView visibility
void WebView_SetHidden(void* handle, bool hidden) {
    @autoreleasepool {
        if (handle == nullptr) return;
        
        @try {
            WKWebView* webView = (__bridge WKWebView*)handle;
            [webView setHidden:hidden];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_SetHidden: %@", exception.reason);
        }
    }
}

// Show Web Inspector
void WebView_ShowInspector(void* handle) {
    @autoreleasepool {
        if (handle == nullptr) return;
        
        @try {
            DragSupportWebView* webView = (__bridge DragSupportWebView*)handle;
            [webView showInspector:nil];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_ShowInspector: %@", exception.reason);
        }
    }
}

void WebView_Focus(void* handle) {
    @autoreleasepool {
        if (handle == nullptr) return;

        @try {
            DragSupportWebView* webView = (__bridge DragSupportWebView*)handle;
            [webView focusForEditingCommands];
        }
        @catch (NSException *exception) {
            NSLog(@"WebViewPlugin: Exception in WebView_Focus: %@", exception.reason);
        }
    }
}

// Validate if a WebView handle is still valid
// Uses a global registry to track valid handles across domain reloads
// This is safe because the registry persists even when Unity's domain reloads
bool WebView_ValidateHandle(void* handle) {
    if (handle == nullptr) {
        return false;
    }
    
    // Check if this handle is in our registry of valid WebViews
    return IsWebViewHandleRegistered(handle);
}

// Check if WebView has valid content loaded (not blank/white screen)
bool WebView_HasValidContent(void* handle) {
    if (handle == nullptr || !IsWebViewHandleRegistered(handle)) {
        return false;
    }
    
    @autoreleasepool {
        DragSupportWebView* webView = (__bridge DragSupportWebView*)handle;
        
        // Check if WebView is still valid and has a superview
        if (webView == nil || webView.superview == nil) {
            return false;
        }
        
        // Check if there was a load failure
        if (webView.navDelegate && webView.navDelegate.hasLoadFailure) {
            return false;
        }
        
        // Check if WebView has a URL loaded
        NSURL* currentURL = webView.URL;
        if (currentURL == nil) {
            return false;
        }
        
        // Check if the URL is not about:blank or empty
        NSString* urlString = [currentURL absoluteString];
        if (urlString == nil || 
            [urlString length] == 0 || 
            [urlString isEqualToString:@"about:blank"]) {
            return false;
        }
        
        // Check loading state - if still loading, consider it valid (not white screen yet)
        if (webView.isLoading) {
            return true;
        }
        
        // Additional check: ensure the view is not hidden
        if (webView.isHidden) {
            return false;
        }
        
        return true;
    }
}

// =============================================================================
// MARK: - Unity Drag Gateway
// =============================================================================
// Transparent view that intercepts Unity GameObject drag events and forwards
// them to the underlying GUIRenderView, while allowing normal mouse events
// to pass through to the WKWebView below.

// Unity 自定义拖拽类型，必须与 DragAndDrop.mm 中一致
static NSString* const kUPPtrArrayPboardType = @"PPtrArrayPboardType";

@interface UnityDragGatewayView : NSView <NSDraggingDestination>
@property (nonatomic, assign) NSView* guiRenderView;
@property (nonatomic, weak) NSView* wkWebView;
@property (nonatomic, assign) BOOL isForwardingToWebView;
@end

@implementation UnityDragGatewayView

- (instancetype)initWithFrame:(NSRect)frame guiRenderView:(NSView*)renderView {
    if (self = [super initWithFrame:frame]) {
        _guiRenderView = renderView;
        _isForwardingToWebView = NO;
        [self registerForDraggedTypes:@[
            kUPPtrArrayPboardType,
            NSPasteboardTypeFileURL,
            @"NSFilenamesPboardType",
            NSPasteboardTypeURL
        ]];
    }
    return self;
}

// ── 关键：普通鼠标/键盘事件穿透给下层 WKWebView ──
- (NSView *)hitTest:(NSPoint)point {
    return nil; // 穿透所有点击事件
}

- (BOOL)isOpaque { 
    return NO; 
}

- (void)drawRect:(NSRect)dirtyRect {
    // 不绘制任何内容
}

// ── Route: Unity drags → GUIRenderView, external drags → WKWebView ──

- (NSView*)dragTargetForSender:(id<NSDraggingInfo>)sender isInitial:(BOOL)isInitial {
    if (isInitial) {
        NSPasteboard* pb = [sender draggingPasteboard];
        _isForwardingToWebView = ![pb availableTypeFromArray:@[kUPPtrArrayPboardType]];
    }
    return _isForwardingToWebView ? _wkWebView : _guiRenderView;
}

- (NSDragOperation)forwardDraggingEntered:(id<NSDraggingInfo>)sender toView:(NSView*)target {
    if (!target || ![target respondsToSelector:@selector(draggingEntered:)]) {
        return NSDragOperationNone;
    }
    @try {
        SEL sel = @selector(draggingEntered:);
        NSMethodSignature *sig = [target methodSignatureForSelector:sel];
        NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
        [inv setSelector:sel]; [inv setTarget:target]; [inv setArgument:&sender atIndex:2];
        [inv invoke];
        NSDragOperation result;
        [inv getReturnValue:&result];
        return result;
    }
    @catch (NSException *e) { return NSDragOperationNone; }
}

- (NSDragOperation)forwardDraggingUpdated:(id<NSDraggingInfo>)sender toView:(NSView*)target {
    if (!target || ![target respondsToSelector:@selector(draggingUpdated:)]) {
        return NSDragOperationNone;
    }
    @try {
        SEL sel = @selector(draggingUpdated:);
        NSMethodSignature *sig = [target methodSignatureForSelector:sel];
        NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
        [inv setSelector:sel]; [inv setTarget:target]; [inv setArgument:&sender atIndex:2];
        [inv invoke];
        NSDragOperation result;
        [inv getReturnValue:&result];
        return result;
    }
    @catch (NSException *e) { return NSDragOperationNone; }
}

- (NSDragOperation)draggingEntered:(id<NSDraggingInfo>)sender {
    NSView* target = [self dragTargetForSender:sender isInitial:YES];
    if (_isForwardingToWebView && ![NSApp isActive]) {
        [NSApp activateIgnoringOtherApps:YES];
    }
    NSDragOperation op = [self forwardDraggingEntered:sender toView:target];
    if (_isForwardingToWebView && op == NSDragOperationNone) {
        op = NSDragOperationCopy;
    }
    return op;
}

- (NSDragOperation)draggingUpdated:(id<NSDraggingInfo>)sender {
    NSView* target = [self dragTargetForSender:sender isInitial:NO];
    NSDragOperation op = [self forwardDraggingUpdated:sender toView:target];
    if (_isForwardingToWebView && op == NSDragOperationNone) {
        op = NSDragOperationCopy;
    }
    return op;
}

- (void)draggingExited:(id<NSDraggingInfo>)sender {
    NSView* target = [self dragTargetForSender:sender isInitial:NO];
    if (target && [target respondsToSelector:@selector(draggingExited:)]) {
        @try { [target performSelector:@selector(draggingExited:) withObject:sender]; }
        @catch (NSException *e) {}
    }
}

- (void)draggingEnded:(id<NSDraggingInfo>)sender {
    NSView* target = [self dragTargetForSender:sender isInitial:NO];
    if (target && [target respondsToSelector:@selector(draggingEnded:)]) {
        @try { [target performSelector:@selector(draggingEnded:) withObject:sender]; }
        @catch (NSException *e) {}
    }
}

- (BOOL)performDragOperation:(id<NSDraggingInfo>)sender {
    NSView* target = [self dragTargetForSender:sender isInitial:NO];
    if (!target || ![target respondsToSelector:@selector(performDragOperation:)]) {
        return NO;
    }
    @try {
        SEL sel = @selector(performDragOperation:);
        NSMethodSignature *sig = [target methodSignatureForSelector:sel];
        NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
        [inv setSelector:sel]; [inv setTarget:target]; [inv setArgument:&sender atIndex:2];
        [inv invoke];
        BOOL result;
        [inv getReturnValue:&result];
        return result;
    }
    @catch (NSException *e) { return NO; }
}

- (BOOL)wantsPeriodicDraggingUpdates {
    return NO;
}

@end

// =============================================================================
// MARK: - Unity Drag Gateway C API
// =============================================================================

extern "C" {

// 安装 Unity Drag Gateway
void* InstallUnityDragGateway(void* wkWebViewPtr, void* guiViewPtr)
{
    @autoreleasepool {
        NSLog(@"UnityDragGateway: InstallUnityDragGateway called");
        NSLog(@"UnityDragGateway: wkWebViewPtr: %p", wkWebViewPtr);
        NSLog(@"UnityDragGateway: guiViewPtr (C++ GUIView): %p", guiViewPtr);
        
        if (!wkWebViewPtr) {
            NSLog(@"UnityDragGateway: ERROR - wkWebViewPtr is NULL!");
            return nullptr;
        }
        
        if (!guiViewPtr) {
            NSLog(@"UnityDragGateway: ERROR - guiViewPtr is NULL!");
            return nullptr;
        }
        
        // 使用 WebViewPlugin 的函数查找真实的 NSView (GUIRenderView)
        NSView* renderView = FindNSViewFromGUIView(guiViewPtr);
        if (!renderView) {
            NSLog(@"UnityDragGateway: ERROR - Could not find NSView for guiViewPtr!");
            return nullptr;
        }
        
        NSLog(@"UnityDragGateway: Found renderView: %@ (%p)", [renderView className], renderView);
        
        NSView* wkWebView = (__bridge NSView*)wkWebViewPtr;
        NSLog(@"UnityDragGateway: wkWebView: %@ (%p)", [wkWebView className], wkWebView);
        
        NSView* parent = wkWebView.superview;
        if (!parent) {
            NSLog(@"UnityDragGateway: ERROR - WKWebView has no superview!");
            return nullptr;
        }
        
        NSLog(@"UnityDragGateway: parent view: %@ (%p)", [parent className], parent);
        
        UnityDragGatewayView* gateway =
            [[UnityDragGatewayView alloc] initWithFrame:wkWebView.frame
                                          guiRenderView:renderView];
        gateway.wkWebView = wkWebView;
        
        // 放在 WKWebView 正上方（z-order 更高）
        [parent addSubview:gateway positioned:NSWindowAbove relativeTo:wkWebView];
        
        // 跟随 WKWebView 自动 resize
        gateway.autoresizingMask = wkWebView.autoresizingMask;
        
        NSLog(@"UnityDragGateway: Gateway installed successfully at %p", gateway);
        
        return (__bridge_retained void*)gateway;
    }
}

void UninstallUnityDragGateway(void* gatewayPtr)
{
    if (!gatewayPtr) return;
    
    @autoreleasepool {
        NSLog(@"UnityDragGateway: Uninstalling gateway at %p", gatewayPtr);
        UnityDragGatewayView* gw =
            (__bridge_transfer UnityDragGatewayView*)gatewayPtr;
        [gw removeFromSuperview];
        NSLog(@"UnityDragGateway: Gateway uninstalled");
    }
}

void UpdateDragGatewayFrame(void* gatewayPtr, float x, float y, float w, float h)
{
    if (!gatewayPtr) return;
    
    UnityDragGatewayView* gw = (__bridge UnityDragGatewayView*)gatewayPtr;
    dispatch_async(dispatch_get_main_queue(), ^{
        gw.frame = NSMakeRect(x, y, w, h);
    });
}

} // extern "C"

