#import <UIKit/UIKit.h>
#import <objc/runtime.h>
#include <dlfcn.h>
#include <mach-o/loader.h>
#include <sys/sysctl.h>
#include <atomic>

@interface UIViewController (SolaceGame)
- (void *)getApp;
- (int)width;
- (int)height;
@end

static IMP originalDrawFrame;
static IMP originalWidth;
static IMP originalHeight;
// The single game window and its nib-owned application delegate live for the process.
static id<UIApplicationDelegate> gameDelegate;
static CGSize lastSize;

namespace {
struct Vec2 { float x, y; };
const uint8_t *clientImage;
std::atomic<uint64_t> drawableSize;

template <typename T>
T clientAddress(size_t offset) {
    return reinterpret_cast<T>(const_cast<uint8_t *>(clientImage + offset));
}

// These offsets and call sites are specific to the UUID checked below and in
// patch_duo.py. Update only the map camera's perspective component. The camera
// system uses its dimensions to build the projection for rendering and raycasts.
float updateMapProjection(void *curve, Vec2 *projection, float zoom) {
    uint64_t size = drawableSize.load(std::memory_order_relaxed);
    if (size) *projection = {float(size >> 32), float(uint32_t(size))};
    return clientAddress<float (*)(void *, float)>(0x27C7C38)(curve, zoom);
}

bool isIPhoneDuo() {
#if TARGET_OS_SIMULATOR
    const char *model = getenv("SIMULATOR_MODEL_IDENTIFIER");
    if (!model) return false;
#else
    char model[32] = {};
    size_t length = sizeof(model);
    if (sysctlbyname("hw.machine", model, &length, nullptr, 0) != 0) return false;
#endif
    return strcmp(model, "iPhone19,4") == 0;
}

void installDuoProjectionHook() {
    Dl_info image;
    if (!dladdr(reinterpret_cast<const void *>(originalDrawFrame), &image)) return;
    auto header = static_cast<const mach_header_64 *>(image.dli_fbase);
    if (!header || header->magic != MH_MAGIC_64) return;
    const uint8_t uuid[] = {0x34, 0xc9, 0x62, 0xcf, 0x17, 0xe7, 0x3d, 0x37,
                            0x9c, 0x1d, 0x58, 0x4d, 0x83, 0x6c, 0x73, 0x15};
    auto command = reinterpret_cast<const load_command *>(header + 1);
    for (uint32_t i = 0; i < header->ncmds; ++i) {
        if (command->cmd == LC_UUID && !memcmp(reinterpret_cast<const uuid_command *>(command)->uuid,
                                              uuid, sizeof(uuid))) {
            clientImage = reinterpret_cast<const uint8_t *>(header);
            break;
        }
        command = reinterpret_cast<const load_command *>(reinterpret_cast<const uint8_t *>(command) + command->cmdsize);
    }
    if (!clientImage) return;
    using MapHook = decltype(&updateMapProjection);
    *clientAddress<MapHook *>(0x4ABF3C8) = updateMapProjection;
}
} // namespace

API_AVAILABLE(ios(13.0))
static CGSize gamePixelSize(UIViewController *controller) {
    UIView *view = controller.viewIfLoaded;
    if (!view.window.windowScene) {
        return CGSizeMake(0, 0);
    }
    CGSize size = view.bounds.size;
    CGFloat scale = view.contentScaleFactor;
    return CGSizeMake((int)(size.width * scale), (int)(size.height * scale));
}

API_AVAILABLE(ios(13.0))
static int gameWidth(UIViewController *controller, SEL selector) {
    CGSize size = gamePixelSize(controller);
    if (size.width > 0) return (int)size.width;
    return ((int (*)(id, SEL))originalWidth)(controller, selector);
}

API_AVAILABLE(ios(13.0))
static int gameHeight(UIViewController *controller, SEL selector) {
    CGSize size = gamePixelSize(controller);
    if (size.height > 0) return (int)size.height;
    return ((int (*)(id, SEL))originalHeight)(controller, selector);
}

API_AVAILABLE(ios(13.0))
static void drawFrame(UIViewController *controller, SEL selector) {
    CGSize size = gamePixelSize(controller);
    if (size.width > 0 && size.height > 0 && !CGSizeEqualToSize(size, lastSize)) {
        if (void *app = [controller getApp]) {
            if (clientImage) {
                drawableSize.store((uint64_t(size.width) << 32) | uint32_t(size.height),
                                   std::memory_order_relaxed);
            }
            // Match the two virtual calls made by 0.33.0's -initView. They queue
            // the renderer and game layout changes on their own engine threads.
            enum { ResizeRendererSlot = 0x80 / sizeof(void *), ResizeGameSlot = 0x20 / sizeof(void *) };
            void *const *methods = *(void ***)app;
            auto resizeRenderer = reinterpret_cast<void (*)(void *, int, int)>(methods[ResizeRendererSlot]);
            auto resizeGame = reinterpret_cast<void (*)(void *, int, int, float)>(methods[ResizeGameSlot]);
            resizeRenderer(app, (int)size.width, (int)size.height);
            resizeGame(app, (int)size.width, (int)size.height, 0);
            lastSize = size;
        }
    }
    ((void (*)(id, SEL))originalDrawFrame)(controller, selector);
}

API_AVAILABLE(ios(13.0))
static void installResizeHooks(void) {
    Class game = NSClassFromString(@"minecraftpeViewControllerBase");
    originalWidth = method_setImplementation(class_getInstanceMethod(game, @selector(width)), (IMP)gameWidth);
    originalHeight = method_setImplementation(class_getInstanceMethod(game, @selector(height)), (IMP)gameHeight);
    originalDrawFrame = method_setImplementation(class_getInstanceMethod(game, @selector(drawFrame)), (IMP)drawFrame);
}

API_AVAILABLE(ios(27.1))
@interface SolaceGameContainer : UIViewController
@property (nonatomic, strong) UIViewController *gameController;
@end

@implementation SolaceGameContainer

- (void)viewDidLoad {
    [super viewDidLoad];
    self.view.backgroundColor = UIColor.blackColor;
    UIViewController *game = self.gameController;
    [self addChildViewController:game];
    game.view.frame = self.view.bounds;
    [self.view addSubview:game.view];
    [game didMoveToParentViewController:self];
}

- (void)viewDidLayoutSubviews {
    [super viewDidLayoutSubviews];
    UIView *view = self.view;
    CGRect viewport = view.bounds;
    CGRect safeArea = UIEdgeInsetsInsetRect(viewport, view.safeAreaInsets);
    CGFloat top = CGRectGetMinY(viewport);
    for (UIViewReservedRegion *region in [view reservedRegionsOfKind:UIViewReservedRegionKind.occlusionRegionKind]) {
        CGRect frame = CGRectIntersection(viewport, region.frame);
        // Keep camera reservations outside the game view. All game screens
        // render and receive touches in the same view-local coordinates.
        if (!CGRectIsEmpty(frame) && CGRectGetMinY(frame) == CGRectGetMinY(viewport) &&
            (CGRectGetMaxX(frame) <= CGRectGetMinX(safeArea) ||
             CGRectGetMinX(frame) >= CGRectGetMaxX(safeArea))) {
            top = MAX(top, CGRectGetMaxY(frame));
        }
    }
    viewport.size.height = CGRectGetMaxY(viewport) - top;
    viewport.origin.y = top;
    if (!CGRectIsEmpty(viewport) && !CGRectEqualToRect(self.gameController.view.frame, viewport)) {
        self.gameController.view.frame = viewport;
    }
}

- (void)viewSafeAreaInsetsDidChange {
    [super viewSafeAreaInsetsDidChange];
    [self.view setNeedsLayout];
}

- (UIViewController *)childViewControllerForStatusBarHidden { return self.gameController; }
- (UIViewController *)childViewControllerForStatusBarStyle { return self.gameController; }
- (UIViewController *)childViewControllerForHomeIndicatorAutoHidden { return self.gameController; }
- (UIViewController *)childViewControllerForScreenEdgesDeferringSystemGestures { return self.gameController; }
- (UIInterfaceOrientationMask)supportedInterfaceOrientations { return self.gameController.supportedInterfaceOrientations; }

@end

API_AVAILABLE(ios(13.0))
@interface SolaceSceneDelegate : UIResponder <UIWindowSceneDelegate>
@property (nonatomic, strong) UIWindow *window;
@end

@implementation SolaceSceneDelegate

- (void)scene:(UIScene *)scene willConnectToSession:(UISceneSession *)session
      options:(UISceneConnectionOptions *)options {
    UIApplication *app = UIApplication.sharedApplication;
    UIWindowScene *windowScene = (UIWindowScene *)scene;
    if (!originalDrawFrame) installResizeHooks();
    BOOL needsLaunch = (gameDelegate == nil);
    if (needsLaunch) {
        // Scene-based UIKit skips the legacy main nib. Its outlets connect the
        // original application delegate, game view controller and window.
        if (!app.delegate.window) {
            NSArray *objects = [NSBundle.mainBundle loadNibNamed:@"MainWindow" owner:app options:nil];
            gameDelegate = app.delegate;
            if (!gameDelegate.window) {
                NSLog(@"Solace: MainWindow.nib did not provide a game window (%@)", objects);
                return;
            }
        } else {
            gameDelegate = app.delegate;
            needsLaunch = NO;
        }
    }
    self.window = gameDelegate.window;
    self.window.windowScene = windowScene;
    self.window.frame = windowScene.coordinateSpace.bounds;
    if (needsLaunch) {
        [gameDelegate application:app didFinishLaunchingWithOptions:nil];
    }
    if (@available(iOS 27.1, *)) {
        if (isIPhoneDuo()) {
            installDuoProjectionHook();
            UIViewController *root = self.window.rootViewController;
            if ([root isKindOfClass:NSClassFromString(@"minecraftpeViewControllerBase")]) {
                SolaceGameContainer *container = [SolaceGameContainer new];
                container.gameController = root;
                self.window.rootViewController = container;
            }
        }
    }
    [self.window makeKeyAndVisible];
    [self scene:scene openURLContexts:options.URLContexts];
    for (NSUserActivity *activity in options.userActivities) {
        [self scene:scene continueUserActivity:activity];
    }
}

- (void)sceneWillEnterForeground:(UIScene *)scene {
    UIApplication *app = UIApplication.sharedApplication;
    id<UIApplicationDelegate> delegate = app.delegate;
    if ([delegate respondsToSelector:@selector(applicationWillEnterForeground:)]) {
        [delegate applicationWillEnterForeground:app];
    }
}

- (void)sceneDidBecomeActive:(UIScene *)scene {
    UIApplication *app = UIApplication.sharedApplication;
    id<UIApplicationDelegate> delegate = app.delegate;
    if ([delegate respondsToSelector:@selector(applicationDidBecomeActive:)]) {
        [delegate applicationDidBecomeActive:app];
    }
}

- (void)sceneWillResignActive:(UIScene *)scene {
    UIApplication *app = UIApplication.sharedApplication;
    id<UIApplicationDelegate> delegate = app.delegate;
    if ([delegate respondsToSelector:@selector(applicationWillResignActive:)]) {
        [delegate applicationWillResignActive:app];
    }
}

- (void)sceneDidEnterBackground:(UIScene *)scene {
    UIApplication *app = UIApplication.sharedApplication;
    id<UIApplicationDelegate> delegate = app.delegate;
    if ([delegate respondsToSelector:@selector(applicationDidEnterBackground:)]) {
        [delegate applicationDidEnterBackground:app];
    }
}

- (void)scene:(UIScene *)scene openURLContexts:(NSSet<UIOpenURLContext *> *)contexts {
    UIApplication *app = UIApplication.sharedApplication;
    id<UIApplicationDelegate> delegate = app.delegate;
    if (![delegate respondsToSelector:@selector(application:openURL:options:)]) return;
    for (UIOpenURLContext *context in contexts) {
        NSMutableDictionary *options = [NSMutableDictionary dictionary];
        options[UIApplicationOpenURLOptionsSourceApplicationKey] = context.options.sourceApplication;
        options[UIApplicationOpenURLOptionsAnnotationKey] = context.options.annotation;
        options[UIApplicationOpenURLOptionsOpenInPlaceKey] = @(context.options.openInPlace);
        [delegate application:app openURL:context.URL options:options];
    }
}

- (void)scene:(UIScene *)scene continueUserActivity:(NSUserActivity *)activity {
    UIApplication *app = UIApplication.sharedApplication;
    id<UIApplicationDelegate> delegate = app.delegate;
    if ([delegate respondsToSelector:@selector(application:continueUserActivity:restorationHandler:)]) {
        [delegate application:app continueUserActivity:activity restorationHandler:^(NSArray *objects) {}];
    }
}

@end
