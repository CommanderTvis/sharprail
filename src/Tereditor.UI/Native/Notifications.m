// User Notifications bridge for Tereditor.UI's MacNotifier. The system attributes a notification to the
// running bundle, so every entry point answers "unavailable" for a process without a bundle identifier
// (an unbundled `dotnet run`), where UNUserNotificationCenter would raise instead.
#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

typedef void (*TereditorNotificationActivated)(const char *identifier);

static TereditorNotificationActivated activated;

@interface TereditorNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end

@implementation TereditorNotificationDelegate
// The app posts only while none of its windows is focused, yet it can still be the frontmost application
// (every window minimised); the system would drop the banner there without this.
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
       willPresentNotification:(UNNotification *)notification
         withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completionHandler {
    completionHandler(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList | UNNotificationPresentationOptionSound);
}

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
didReceiveNotificationResponse:(UNNotificationResponse *)response
         withCompletionHandler:(void (^)(void))completionHandler {
    TereditorNotificationActivated callback = activated;
    if (callback && [response.actionIdentifier isEqualToString:UNNotificationDefaultActionIdentifier])
        callback(response.notification.request.identifier.UTF8String);
    completionHandler();
}
@end

static TereditorNotificationDelegate *delegate;

static UNUserNotificationCenter *Center(void) {
    if (NSBundle.mainBundle.bundleIdentifier.length == 0) return nil;
    @try { return UNUserNotificationCenter.currentNotificationCenter; }
    @catch (NSException *exception) { return nil; }
}

static NSString *Text(const char *value) {
    return value ? [NSString stringWithUTF8String:value] ?: @"" : @"";
}

// Installs the click callback. Returns 1 when this process can post notifications, 0 otherwise.
int tereditor_notifications_start(TereditorNotificationActivated callback) {
    @autoreleasepool {
        UNUserNotificationCenter *center = Center();
        if (!center) return 0;
        activated = callback;
        if (!delegate) delegate = [TereditorNotificationDelegate new];
        center.delegate = delegate;
        return 1;
    }
}

// Shows the system's permission prompt if the user has not answered it yet.
void tereditor_notifications_authorize(void) {
    @autoreleasepool {
        [Center() requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound)
                                completionHandler:^(BOOL granted, NSError *error) {}];
    }
}

// Posts a notification, replacing a delivered one with the same identifier. Asks for permission first
// when it is undetermined; a denied permission drops the notification.
void tereditor_notifications_show(const char *identifier, const char *title, const char *subtitle, const char *body) {
    @autoreleasepool {
        UNUserNotificationCenter *center = Center();
        if (!center) return;
        UNMutableNotificationContent *content = [UNMutableNotificationContent new];
        content.title = Text(title);
        content.subtitle = Text(subtitle);
        content.body = Text(body);
        content.sound = UNNotificationSound.defaultSound;
        UNNotificationRequest *request = [UNNotificationRequest requestWithIdentifier:Text(identifier) content:content trigger:nil];
        [center requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound)
                              completionHandler:^(BOOL granted, NSError *error) {
            if (granted) [center addNotificationRequest:request withCompletionHandler:nil];
        }];
    }
}

// A terminal bell while the app is in the background: one dock bounce, as Ghostty's bell attention feature.
// Works without a bundle; AppKit is driven on the main thread.
void tereditor_request_attention(void) {
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!NSApp.isActive) [NSApp requestUserAttention:NSInformationalRequest];
    });
}

// A terminal bell's system feature: the user's alert sound.
void tereditor_beep(void) {
    dispatch_async(dispatch_get_main_queue(), ^{ NSBeep(); });
}
