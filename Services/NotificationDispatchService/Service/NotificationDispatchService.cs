using Database.Enums;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Services.NotificationDispatch.Interface;
using Services.NotificationDispatch.Models;
using Services.Presence.Interface;
using Services.Repositories.DeviceNotificationConfig.Interface;
using Services.Repositories.User.Interface;
using Services.Utilities.Statics;
using Services.Constants;
using Services.Contacts.Interface;
using Services.Contacts.Models;

namespace Services.NotificationDispatch.Service
{
	/// <inheritdoc/>
	public class NotificationDispatchService(IDeviceNotificationConfigRepository deviceNotificationConfigRepo, IUserRepository userRepo, IAppleService appleService, IPresenceService presenceService, IProfileVisibility profiles, ILogger<NotificationDispatchService> logger) : INotificationDispatch
	{
		private async Task SendPushNotificationToApple(string deviceToken, NotificationPayload notificationInfo)
		{
			AppleNotificationPayload payload = new()
			{
				Aps = new ApplePushNotification
				{
					Alert = new Alert
					{
						Title = notificationInfo.Title,
						Body = notificationInfo.Body,
					},
					Sound = "default",
				},
				Route = notificationInfo.Route,
				// "chatId" is provided only when the notification is for a chat;
				// in other cases, only the name of the screen to which the user should be navigated
				// upon opening the notification is provided.
				ChatId = notificationInfo.ChatId?.ToString() ?? string.Empty,
			};

			await appleService.SendPushNotification(deviceToken, payload);
		}

		private async Task SendPushNotificationToAndroid(string deviceToken, NotificationPayload notificationInfo)
		{
			var messaging = FirebaseMessaging.DefaultInstance;
			if (messaging == null)
			{
				logger.LogError("Firebase is not initialized. Cannot send Android push notification to device: {DeviceToken}", deviceToken);
				return;
			}

			var message = new Message()
			{
				Notification = new Notification
				{
					Title = notificationInfo.Title,
					Body = notificationInfo.Body,
				},
				Token = deviceToken,
				Data = new Dictionary<string, string>()
				{
					{"route", notificationInfo.Route},
                    // "chatId" is provided only when the notification is for a chat;
                    // in other cases, only the name of the screen to which the user should be navigated
                    // upon opening the notification is provided.
                    {"chatId", notificationInfo.ChatId?.ToString() ?? string.Empty}
				}
			};

			logger.LogDebug("Sending Android push notification to device: {DeviceToken}", deviceToken);
			string messageId = await messaging.SendAsync(message);
			logger.LogInformation("Android push notification sent successfully. MessageId: {MessageId}", messageId);
		}

		//  IMPORTANT: There is a possibility that, with the change in the ability to manage which notifications
		//  to receive and which not, the method may start receiving an array of data for each device to which it
		//  should send a notification as the first parameter.
		private async Task SendNotification(List<DeviceData> deviceDataList, NotificationPayload notificationBody)
		{
			foreach (var deviceData in deviceDataList)
			{
				string deviceToken = deviceData.Token;
				try
				{
					if (deviceData.OS == OperatingSystemType.ios)
					{
						await SendPushNotificationToApple(deviceToken, notificationBody);
						continue;
					}

					if (deviceData.OS == OperatingSystemType.android)
					{
						await SendPushNotificationToAndroid(deviceToken, notificationBody);
						continue;
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Failed to send push notification to device {DeviceToken} (OS: {OS})", deviceToken, deviceData.OS);
				}
			}
		}

		private async Task NotifyOfflineUser(int userId, NotificationPayload notificationBody)
		{
			if (await presenceService.IsUserOnline(userId)) return;

			List<DeviceData> result = await deviceNotificationConfigRepo.FetchEnabledUserDeviceDataById(userId);

			await SendNotification(result, notificationBody);
		}

		private async Task NotifyOfflineUsers(int[] receiversIds, int chatOwenId, NotificationPayload notificationBody)
		{
			int[] offlineReceivers = await presenceService.FilterOfflineUsers(receiversIds, chatOwenId);

			if (offlineReceivers.Length == 0) return;

			List<DeviceData> userDevices = await deviceNotificationConfigRepo.FetchEnabledUsersDevicesDataByIds(offlineReceivers);

			await SendNotification(userDevices, notificationBody);
		}

		private async Task<string> GetUserNameById(int userId) =>
			await userRepo.GetUserNameById(userId) ?? "";

		/// <summary>
		/// Stage 09 (plan/09-contacts-privacy.md: "Неприятелите се представят като unknown във всички
		/// изходи, включително ... известия"). A push is per recipient, so the subject's name is resolved
		/// per recipient too: the SAME group message is titled with the sender's name for the recipients
		/// that have a disclosing relation with them and with "unknown" for the strangers in that group.
		/// Split once for the whole audience rather than resolved per device.
		/// </summary>
		private async Task NotifyOfflineUsersWithSubjectAsync(
			int[] receiversIds, int subjectUserId, Func<string, NotificationPayload> payloadForName)
		{
			if (receiversIds.Length == 0) return;

			IReadOnlySet<int> knowsSubject = await profiles.ViewersWhoKnowAsync(subjectUserId, receiversIds);

			int[] knowing = [.. receiversIds.Where(knowsSubject.Contains)];
			int[] strangers = [.. receiversIds.Where(id => !knowsSubject.Contains(id))];

			if (knowing.Length > 0)
			{
				await NotifyOfflineUsers(knowing, subjectUserId, payloadForName(await GetUserNameById(subjectUserId)));
			}

			if (strangers.Length > 0)
			{
				await NotifyOfflineUsers(strangers, subjectUserId, payloadForName(ProfileIdentity.UnknownName));
			}
		}

		/// <summary>One recipient: the same rule, asked for that one viewer.</summary>
		private async Task<string> SubjectNameForAsync(int recipientUserId, int subjectUserId) =>
			(await profiles.ResolveAsync(recipientUserId, subjectUserId)).Name;


		private static NotificationPayload ConstructNotificationPayload(string title, string body, string route)
		{
			return new()
			{
				Title = title,
				Body = body,
				Route = route,
			};
		}

		private static NotificationPayload ConstructChatNotificationPayload(string title, string body, string route, int chatId)
		{
			NotificationPayload newPayload = ConstructNotificationPayload(title, body, route);
			newPayload.ChatId = chatId;
			return newPayload;
		}

		// TODO Nikolay: 20.06.2025 --> Refactoring: These hardcoded strings should be moved out
		public Task NotificationForNewMessage(int[] receiversIds, int senderUserId, string message, int chatId) =>
			NotifyOfflineUsersWithSubjectAsync(receiversIds, senderUserId, name =>
				ConstructChatNotificationPayload($"{name} send you a message", message, ScreenNames.ChatScreen, chatId));

		public Task NotificationForNewChat(int[] chatParticipantIds, int chatCreatorId, int chatId) =>
			NotifyOfflineUsersWithSubjectAsync(chatParticipantIds, chatCreatorId, name =>
				ConstructChatNotificationPayload("New Created chat", $"{name} created chat with you.", ScreenNames.ChatScreen, chatId));

		public async Task NotificationForNewFriendshipRequest(int userId, string name)
		{
			NotificationPayload notificationPayload = ConstructNotificationPayload("New Request", $"{name} you send new friendship request", ScreenNames.RequestsScreen);

			await NotifyOfflineUser(userId, notificationPayload);
		}

		public async Task NotificationAcceptFriendship(int notificationRecipientId, int requestSenderId)
		{
			string userName = await SubjectNameForAsync(notificationRecipientId, requestSenderId);

			NotificationPayload notificationPayload = ConstructNotificationPayload("Accepted request", $"{userName} accepted your friend request", ScreenNames.ContactsScreen);

			await NotifyOfflineUser(notificationRecipientId, notificationPayload);
		}

		public async Task NotificationForRejectedFriendship(int notificationRecipientId, int requestSenderId)
		{
			string userName = await SubjectNameForAsync(notificationRecipientId, requestSenderId);

			NotificationPayload notificationPayload = ConstructNotificationPayload("Rejected request", $"{userName} reject your friend request", ScreenNames.RejectedRequestsScreen);

			await NotifyOfflineUser(notificationRecipientId, notificationPayload);
		}

		public async Task NotificationForBlockedFriendship(int notificationRecipientId, int requestSenderId)
		{
			// The blocked party no longer holds a disclosing relation with its blocker, so this resolves to
			// the "unknown" placeholder - the notification stays, the identity does not.
			string userName = await SubjectNameForAsync(notificationRecipientId, requestSenderId);

			NotificationPayload notificationPayload = ConstructNotificationPayload("Blocker request", $"{userName} block your friend request", ScreenNames.BlockedContactsScreen);

			await NotifyOfflineUser(notificationRecipientId, notificationPayload);
		}
	}
}
