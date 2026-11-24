#import <UIKit/UIKit.h>
#import <objc/runtime.h>

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
        void *app = [controller getApp];
        if (app) {
            // Match the two virtual calls made by 0.33.0's -initView. They queue
            // the renderer and game layout changes on their own engine threads.
            enum { ResizeRendererSlot = 0x80 / sizeof(void *), ResizeGameSlot = 0x20 / sizeof(void *) };
            void *const *methods = *(void ***)app;
            void (*resizeRenderer)(void *, int, int) = methods[ResizeRendererSlot];
            void (*resizeGame)(void *, int, int, float) = methods[ResizeGameSlot];
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
        // Genoa screens (including the map HUD) bypass the engine's safe-zone
        // panel. Keep the drawable and its touch coordinates together below
        // a corner reservation, while preserving the layout on notched phones.
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
        UIViewController *root = self.window.rootViewController;
        if ([root isKindOfClass:NSClassFromString(@"minecraftpeViewControllerBase")]) {
            SolaceGameContainer *container = [SolaceGameContainer new];
            container.gameController = root;
            self.window.rootViewController = container;
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
