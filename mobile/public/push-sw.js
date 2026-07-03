// Web Push handling for the DevThrottle mobile Progressive Web App.
//
// This file is imported into the Workbox-generated service worker via the plugin's
// workbox.importScripts option (see vite.config.ts), so it runs in the service worker global scope
// and only adds event listeners - it does not touch the precache/offline behavior Workbox owns.
//
// The Gateway pushes a { "count": N } message whenever the number of sessions that "need you" is
// above zero and has changed. We turn that into the app-icon dot two ways, because the platforms
// differ:
//   - iOS installed PWA + desktop Chrome/Edge: navigator.setAppBadge(N) sets a real badge.
//   - Android: the Badging API is not supported; the launcher draws a dot ONLY while a notification
//     is showing. So we show one silent, tagged notification, which IS the Android dot.
// The dot is cleared by the app itself when it comes to the foreground and finds nothing waiting
// (the Gateway never pushes a zero, because a push that shows no notification is penalized by
// browsers - the userVisibleOnly contract).

'use strict';

var NEEDS_YOU_TAG = 'devthrottle-needs-you';

self.addEventListener('push', function (event) {
  var count = 0;
  if (event.data) {
    try {
      var payload = event.data.json();
      count = Number(payload && payload.count) || 0;
    } catch (e) {
      count = 0;
    }
  }
  event.waitUntil(applyNeedsYou(count));
});

function applyNeedsYou(count) {
  var tasks = [];

  // App badge: iOS installed PWA + desktop. Not present on Android (harmless - the notification
  // below is what makes the dot appear there).
  if (self.navigator && 'setAppBadge' in self.navigator) {
    if (count > 0) {
      tasks.push(self.navigator.setAppBadge(count).catch(function () {}));
    } else {
      tasks.push(self.navigator.clearAppBadge().catch(function () {}));
    }
  }

  if (count <= 0) {
    tasks.push(closeNeedsYou());
    return Promise.all(tasks);
  }

  // Always show one silent, tagged notification while a session needs you. On Android the launcher
  // draws the app-icon dot ONLY while a notification is present, so this is the dot - we must show it
  // even if the app happens to be open right now, otherwise there is no dot the moment the user
  // leaves the app. The shared tag replaces (never stacks) it, and silent + renotify:false keep it a
  // quiet dot that never buzzes on updates. The app clears it when it comes to the foreground with
  // nothing waiting (see reconcileBadge in push/register.ts).
  var body = count === 1 ? '1 session needs you' : count + ' sessions need you';
  tasks.push(
    self.registration.showNotification('DevThrottle', {
      tag: NEEDS_YOU_TAG,
      body: body,
      renotify: false,
      silent: true,
      icon: '/m/icon-192.png',
      badge: '/m/icon-192.png',
      data: { url: '/m/' }
    })
  );

  return Promise.all(tasks);
}

function closeNeedsYou() {
  return self.registration.getNotifications({ tag: NEEDS_YOU_TAG }).then(function (list) {
    list.forEach(function (n) {
      n.close();
    });
  });
}

self.addEventListener('notificationclick', function (event) {
  event.notification.close();
  var url = (event.notification.data && event.notification.data.url) || '/m/';
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clients) {
      for (var i = 0; i < clients.length; i++) {
        var c = clients[i];
        if (c.url.indexOf('/m') !== -1 && 'focus' in c) {
          return c.focus();
        }
      }
      if (self.clients.openWindow) {
        return self.clients.openWindow(url);
      }
      return undefined;
    })
  );
});

// The page posts this when it is foregrounded and the live roster shows nothing waiting, so the dot
// clears even though the Gateway only ever pushes non-zero counts.
self.addEventListener('message', function (event) {
  if (event.data && event.data.type === 'devthrottle-clear-needs-you') {
    var tasks = [closeNeedsYou()];
    if (self.navigator && 'clearAppBadge' in self.navigator) {
      tasks.push(self.navigator.clearAppBadge().catch(function () {}));
    }
    event.waitUntil(Promise.all(tasks));
  }
});
