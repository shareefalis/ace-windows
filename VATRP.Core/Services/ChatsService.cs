﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows.Input;
using VATRP.Core.Enums;
using VATRP.Core.Events;
using VATRP.Core.Extensions;
using VATRP.Core.Interfaces;
using VATRP.Core.Model;
using VATRP.Core.Model.Utils;
using VATRP.LinphoneWrapper.Enums;

namespace VATRP.Core.Services
{
    public class ChatsService : IChatService
    {
        private readonly IContactsService _contactSvc;
        private readonly ILinphoneService _linphoneSvc;
        private readonly ServiceManagerBase _serviceManager;

        private ObservableCollection<VATRPChat> _items;

        private ObservableCollection<VATRPContact> _contacts;

        public static readonly int InputUserDelayTypingTimeout = 3;
        public static readonly int IntervalTypingTimeout = 5;

        public event EventHandler<ConversationEventArgs> ConversationClosed;

        public event EventHandler<ConversationEventArgs> ConversationStateChanged;

        public event EventHandler<ConversationEventArgs> ConversationUnReadStateChanged;

        public event EventHandler<ConversationUpdatedEventArgs> ConversationUpdated;

        public event EventHandler<EventArgs> ContactsChanged;

        public event EventHandler<ContactEventArgs> ContactAdded;

        public event EventHandler<ContactRemovedEventArgs> ContactRemoved;

        public event EventHandler<ConversationEventArgs> NewConversationCreated;

        public ChatsService(ServiceManagerBase mngBase)
        {
            this._serviceManager = mngBase;
            this._contactSvc = _serviceManager.ContactService;
            this._linphoneSvc = _serviceManager.LinphoneService;
            this._contactSvc.ContactRemoved += OnContactRemoved;
            this._contactSvc.ContactsChanged += OnContactsChanged;
            this._items = new ObservableCollection<VATRPChat>();

            this._linphoneSvc.IsComposingReceivedEvent += OnChatMessageComposing;
            this._linphoneSvc.OnChatMessageReceivedEvent += OnChatMessageReceived;
            this._linphoneSvc.OnChatMessageStatusChangedEvent += OnChatStatusChanged;
        }

        private void OnContactsChanged(object sender, EventArgs eventArgs)
        {
            foreach (var contact in this._contactSvc.Contacts)
            {
                VATRPContact chatContact = FindContact(new ContactID(contact.ID, contact.NativePtr));
                if (chatContact == null)
                {
                    AddChat(contact, contact.ID);
                    Contacts.Add(contact);
                }
            }

            foreach (var chatContact in this.Contacts)
            {
                VATRPContact contact = this._contactSvc.FindContact(new ContactID(chatContact.ID, chatContact.NativePtr));
                if (contact == null)
                {
                    RemoveChat(GetChat(chatContact));
                    Contacts.Remove(chatContact);
                }
            }

            if (ContactsChanged != null)
                ContactsChanged(this, null);
        }

        public void OnContactRemoved(object sender, ContactRemovedEventArgs e)
        {
            VATRPContact contact = this._contactSvc.FindContact(e.contactId);
            if (contact != null)
            {
                VATRPChat chat = FindChat(contact);
                if (chat != null)
                {
                    if (ConversationClosed != null)
                        ConversationClosed(this, new ConversationEventArgs(chat));
                    RemoveChat(chat);
                }
                Contacts.Remove(contact);
                if (ContactRemoved != null)
                    ContactRemoved(this, new ContactRemovedEventArgs(new ContactID(contact), true));
            }
        }

        private void OnChatMessageComposing(string remoteUser, IntPtr chatPtr, uint rttCode)
        {
            string dn, un, host;
            int port;

            System.Windows.Threading.Dispatcher dispatcher = null;
            try
            {
                dispatcher = this._serviceManager.Dispatcher;
            }
            catch (NullReferenceException)
            {
                return;
            }
            if (dispatcher != null)
                dispatcher.BeginInvoke((Action) delegate()
                {
                    if (!VATRPCall.ParseSipAddressEx(remoteUser, out dn, out un,
                        out host, out port))
                        un = "";

                    if (!un.NotBlank() )
                        return;
                    VATRPContact contact = FindContact(new ContactID(un, IntPtr.Zero));

                    if (contact == null)
                    {
                        contact = new VATRPContact(new ContactID(un, IntPtr.Zero));
                        contact.DisplayName = dn;
                        contact.Fullname = un;
                        _contactSvc.AddContact(contact, "");
                        Contacts.Add(contact);
                        if (ContactAdded != null)
                            ContactAdded(this, new ContactEventArgs(new ContactID(contact)));
                    }

                    VATRPChat chat = GetChat(contact);

                    chat.UnreadMsgCount++;

                    VATRPChatMessage message = chat.SearchIncompleteMessage(MessageDirection.Incoming);
                    if (message == null)
                    {
                        message = new VATRPChatMessage(MessageContentType.Text)
                        {
                            Direction = MessageDirection.Incoming,
                            Sender = contact.Fullname,
                            Receiver = chat.Contact.Fullname,
                            IsIncompleteMessage = true,
                            Chat = chat
                        };

                        chat.AddMessage(message);
                    }
                    else
                    {
                        message.MessageTime = DateTime.Now;
                    }

                    char rcvdRtt = '\0';
                    try
                    {
                        rcvdRtt = Convert.ToChar(rttCode);
                        var sb = new StringBuilder(message.Content);
                        switch (rcvdRtt)
                        {
                            case '\n':
                            case '\r':
                                message.IsIncompleteMessage = false;
                                break;
                            case '\b':
                                sb.Remove(sb.Length - 1, 1);
                                break;
                            default:
                                sb.Append(rcvdRtt);
                                break;
                        }
                        if (message.IsIncompleteMessage)
                            message.Content = sb.ToString();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Error in OnChatMessageComposing: " + ex.Message);
                        message.IsIncompleteMessage = false;
                    }
                    if (!message.Content.NotBlank())
                        chat.DeleteMessage(message);
                    else
                        chat.UpdateLastMessage();
                    this.OnConversationUpdated(chat, true);
                });
        }

        private void OnChatMessageReceived(IntPtr chatPtr, string remoteUser, VATRPChatMessage chatMessage)
        {
            string dn, un, host;
            int port;

            System.Windows.Threading.Dispatcher dispatcher = null;
            try
            {
               dispatcher = this._serviceManager.Dispatcher;
            }
            catch (NullReferenceException)
            {
                return;
            }

            if (dispatcher != null)
                dispatcher.BeginInvoke((Action) delegate()
                {
                    if (!VATRPCall.ParseSipAddressEx(remoteUser, out dn, out un,
                        out host, out port))
                        un = "";

                    if (!un.NotBlank())
                        return;

                    VATRPContact contact = FindContact(new ContactID(un, IntPtr.Zero));

                    if (contact == null)
                    {
                        contact = new VATRPContact(new ContactID(un, IntPtr.Zero)) {DisplayName = dn, Fullname = un};
                        _contactSvc.AddContact(contact, "");
                        Contacts.Add(contact);
                        if (ContactAdded != null)
                            ContactAdded(this, new ContactEventArgs(new ContactID(contact)));
                    }

                    VATRPChat chat = GetChat(contact);
                    chat.NativePtr = chatPtr;

                    chat.UnreadMsgCount++;
                    contact.UnreadMsgCount += chat.UnreadMsgCount;

                    chatMessage.Chat = chat;
                    chat.AddMessage(chatMessage);
                    chat.UpdateLastMessage();

                    this.OnConversationUpdated(chat, true);
                });
        }

        private void OnChatStatusChanged(IntPtr chatMsgPtr, LinphoneChatMessageState state)
        {
            System.Windows.Threading.Dispatcher dispatcher = null;
            try
            {
                dispatcher = this._serviceManager.Dispatcher;
            }
            catch (NullReferenceException)
            {
                return;
            }
            if (dispatcher != null)
                dispatcher.BeginInvoke((Action) delegate()
                {
                    lock (this._items)
                    {
                        foreach (var chatItem in this._items)
                        {
                            var chatMessage = chatItem.FindMessage(chatMsgPtr);
                            if (chatMessage != null)
                            {
                                chatMessage.Status = state;
                                return;
                            }
                        }
                    }
                });
        }

        private VATRPChat AddChat(VATRPContact contact, string dialogId)
        {
            VATRPChat item = null;
            if (contact != null)
            {
                item = this.FindChat(contact);
                if (item == null)
                {
                    item = this.CreateChat(contact, dialogId);
                }
            }
            return item;
        }

        private void CheckTopPosition(VATRPChat chat)
        {
            if ((chat != null) && (this._items.IndexOf(chat) > 0))
            {
                this._items.ReplaceToTop<VATRPChat>(chat);
            }
        }

        public void ClearChatMsgs(ChatID chatID)
        {
            if (chatID != null)
            {
                VATRPChat chat = this.FindChat(chatID);
                if (chat != null)
                {
                    chat.Messages.Clear();
                }
            }
        }

        public void CloseAllChats()
        {
            Debug.WriteLine("asyncOperation ChatsManager.CloseAllChats");
            for (int i = 0; i < this._items.Count; i++)
            {
                VATRPChat chat = this._items[i];
                this.CloseChat(chat);
                i--;
            }
        }

        public void CloseChat(VATRPChat chat)
        {
            if (chat != null)
            {
                this.RemoveChat(chat);
                this.OnConversationClosed(chat);
            }
        }

        public void CloseChat(ContactID contactID)
        {
            if (contactID != null)
            {
                VATRPChat chat = this.FindChat(contactID);
                if (chat != null)
                {
                    this.CloseChat(chat);
                }
            }
        }

        public VATRPChat CreateChat(VATRPContact contact)
        {
            if (contact == null)
            {
                return null;
            }
            return this.CreateChat(contact, string.Empty);
        }

        public VATRPChat CreateChat(VATRPContact contact, string dialogId)
        {
            if (contact == null)
            {
                return null;
            }

            VATRPChat item = new VATRPChat(contact, dialogId);
            _items.InsertToTop<VATRPChat>(item);
            this.OnNewConversationCreated(item);
            return item;
        }

        public VATRPChat FindChat(VATRPContact contact)
        {
            if (contact == null)
            {
                return null;
            }
            return this.FindChat(new ContactID(contact.ID, contact.NativePtr));
        }

        private VATRPChat FindChat(IntPtr chatPtr)
        {
            if (chatPtr == IntPtr.Zero)
            {
                return null;
            }
            lock (this._items)
            {
                foreach (VATRPChat chatItem in this._items)
                {
                    if ((chatItem != null) && chatItem.NativePtr == chatPtr)
                    {
                        return chatItem;
                    }
                }
            }

            return null;
        }

        private VATRPChat FindChat(VATRPChat chat)
        {
            if (chat == null)
                return null;

            lock (this._items)
            {
                foreach (VATRPChat chatItem in this._items)
                {
                    if ((chatItem != null) && chatItem.ID == chat.ID)
                    {
                        return chatItem;
                    }
                }
            }

            return null;
        }

        public VATRPChat FindChat(ChatID chatID)
        {
            if (chatID == null)
                return null;

            lock (this._items)
            {
                foreach (VATRPChat chatItem in this._items)
                {
                    if ((chatItem != null) && chatItem == chatID)
                    {
                        return chatItem;
                    }
                }
            }

            return null;
        }

        public VATRPChat FindChat(ContactID contactID)
        {
            if (contactID == null)
                return null;

            lock (this._items)
            {
                foreach (VATRPChat chatItem in this._items)
                {
                    if ((chatItem != null) && chatItem.ID == contactID.ID)
                    {
                        return chatItem;
                    }
                }
            }

            return null;
        }

        public List<VATRPChat> GetAllConversations()
        {
            if (this._items == null)
            {
                return new List<VATRPChat>();
            }
            return Enumerable.ToList<VATRPChat>((IEnumerable<VATRPChat>) this._items);
        }

        public VATRPChat GetChat(VATRPContact contact)
        {
            VATRPChat chat = this.FindChat(contact);
            if (chat == null)
            {
                chat = this.AddChat(contact, string.Empty);
                this.Contacts.Add(contact);
                if (ContactAdded != null)
                    ContactAdded(this, new ContactEventArgs(new ContactID(contact)));
            }
            return chat;
        }

        public VATRPContact GetFirstContactWithUnreadMessages()
        {
            foreach (VATRPChat chat in this._items)
            {
                if (chat.HasUnreadMsg)
                {
                    return chat.Contact;
                }
            }
            return null;
        }

        private List<VATRPChatMessage> GetLastUnreadMessages()
        {
            var list = new List<VATRPChatMessage>();
            foreach (VATRPChat chat in this._items)
            {
                if (chat.HasUnreadMsg)
                {
                    int num = 0;
                    for (int j = chat.Messages.Count - 1; j >= 0; j--)
                    {
                        if (num >= chat.UnreadMsgCount)
                        {
                            break;
                        }
                        if (chat.Messages[j].Direction == MessageDirection.Incoming)
                        {
                            list.Add(chat.Messages[j]);
                            num++;
                        }
                    }
                }
            }
            list.Sort();
            var list2 = new List<VATRPChatMessage>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list2.Count == 5)
                {
                    return list2;
                }
                if (list2.Count == 0)
                {
                    list2.Add(list[i]);
                }
                else
                {
                    list2.Insert(0, list[i]);
                }
            }
            return list2;
        }

        public uint GetUnreadMsgsCount()
        {
            uint num = 0;
            lock (this._items)
            {
                foreach (VATRPChat chat in this._items)
                {
                    if ((chat != null) && (chat.UnreadMsgCount > 0))
                    {
                        num += chat.UnreadMsgCount;
                    }
                }
            }
            return num;
        }

        public static bool IsMessageAlreadyExistCheckByMsgID(VATRPChat chat, string msgID)
        {
            if ((chat != null) && !string.IsNullOrEmpty(msgID))
            {
                foreach (VATRPChatMessage message in chat.Messages)
                {
                    if (message.ID == msgID)
                    {
                        Debug.WriteLine("ChatList_Manager:IsMessageAlreadyExistCheckByMsgID: msg exist msgId = " + 
                            msgID);
                        return true;
                    }
                }
            }
            return false;
        }

        private void OnConversationClosed(VATRPChat chat)
        {
            if ((chat != null) && (this.ConversationClosed != null))
            {
                this.ConversationClosed(this, new ConversationEventArgs(chat));
            }
        }

        private void OnConversationStateChanged(VATRPChat chat)
        {
            if ((chat != null) && (this.ConversationStateChanged != null))
            {
                this.ConversationStateChanged(this, new ConversationEventArgs(chat));
            }
        }

        private void OnConversationUnReadStateChanged(VATRPChat chat)
        {
            if ((chat != null) && (this.ConversationUnReadStateChanged != null))
            {
                this.ConversationUnReadStateChanged(this, new ConversationEventArgs(chat));
            }
        }

        public void OnConversationUpdated(VATRPChat chat, bool allowUpdate)
        {
            if (chat != null)
            {
                if (this.ConversationUpdated != null)
                {
                    var args = new ConversationUpdatedEventArgs(chat);
                    args.AllowToChangeUnreadMessageCounter = allowUpdate;
                    this.ConversationUpdated(this, args);
                }
            }
        }

        public void MarkChatAsRead(ChatID chatId)
        {
            VATRPChat chat = FindChat(chatId);
            if (chat != null)
            {
                chat.UnreadMsgCount = 0;
                if ( chat.Contact != null )
                {
                    chat.Contact.UnreadMsgCount = 0;
                }
                _linphoneSvc.MarkChatAsRead(chat.NativePtr);
            }
        }

        public VATRPContact FindContact(ContactID contactID)
        {
            foreach (var contactItem in Contacts)
            {
                if (contactItem.ID == contactID.ID)
                {
                    return contactItem;
                }
            }

            return null;
        }

        private void OnNewConversationCreated(VATRPChat chat)
        {
            if ((chat != null) && (this.NewConversationCreated != null))
            {
                this.NewConversationCreated(this, new ConversationEventArgs(chat));
            }
        }

        private bool RemoveChat(VATRPChat chat)
        {
            if (chat == null)
            {
                return false;
            }
            return this._items.Remove(chat);
        }

        public bool ComposeAndSendMessage(IntPtr callPtr, VATRPChat chat, char key, bool inCompleteMessage)
        {
            VATRPChat chatID = this.FindChat(chat);
            if ((chatID == null) || (chatID.Contact == null))
            {
                return false;
            }

            VATRPContact loggedContact = _contactSvc.FindLoggedInContact();

            
            var message = chat.SearchIncompleteMessage(MessageDirection.Outgoing);
            if (message == null)
            {
                message = new VATRPChatMessage(MessageContentType.Text)
                {
                    Direction = MessageDirection.Outgoing,
                    Status = LinphoneChatMessageState.LinphoneChatMessageStateIdle,
                    Receiver = chat.Contact.Fullname,
                    Sender = loggedContact != null ? loggedContact.Fullname : "unknown sender",
                    IsRTTMessage = true
                };
                chatID.AddMessage(message);
            }
            else
            {
                message.MessageTime = DateTime.Now;
            }

            var rttCode = (uint) key;
            var createBubble = false;
            if (key != '\r')
            {
                var sb = new StringBuilder(message.Content);
                if (key == '\b')
                {
                    if (sb.Length > 0)
                        sb.Remove(sb.Length - 1, 1);
                }
                else
                    sb.Append(Convert.ToChar(rttCode));
                message.Content = sb.ToString();
                chat.UpdateLastMessage();
            }
            else
            {
                createBubble = true;
            }
 
            message.IsIncompleteMessage = inCompleteMessage;
            
            // send message to linphone
            var chatPtr = chat.NativePtr;
            var msgPtr = message.NativePtr;
            _linphoneSvc.SendChar(rttCode, callPtr, ref chatPtr, ref msgPtr);

            chat.NativePtr = chatPtr;
            message.NativePtr = msgPtr;
            if (message.NativePtr != IntPtr.Zero)
                message.Status = _linphoneSvc.GetMessageStatus(message.NativePtr);

            if (createBubble && !message.Content.NotBlank())
            {
                // delete empty message 
                chatID.DeleteMessage(message);
            }
            this.OnConversationUpdated(chatID, true);
            return true;

        }

        public bool ComposeAndSendMessage(VATRPChat chat, string text)
        {
            VATRPChat chatID = this.FindChat(chat);
            if ((chatID == null) || (chatID.Contact == null))
            {
                return false;
            }

            VATRPContact loggedContact = _contactSvc.FindLoggedInContact();

            var message = new VATRPChatMessage(MessageContentType.Text)
            {
                Direction = MessageDirection.Outgoing,
                Status = LinphoneChatMessageState.LinphoneChatMessageStateIdle,
                Receiver = chat.Contact.Fullname,
                Sender = loggedContact != null ? loggedContact.Fullname : "unknown sender",
                MessageTime = DateTime.Now,
                IsIncompleteMessage = false,
                Content = text
            };

            chat.AddMessage(message);
            chat.UpdateLastMessage();

            this.OnConversationUpdated(chat, true);

            // send message to linphone
            IntPtr msgPtr = IntPtr.Zero;
            _linphoneSvc.SendChatMessage(chat, message.Content, ref msgPtr);
            message.NativePtr = msgPtr;
            return true;
        }

        private void SetOfflineStateToChat(VATRPChat chat)
        {
            if (chat != null)
            {
                if (chat.Contact != null)
                {
                    chat.Contact.Status = UserStatus.Offline;
                }

            }
        }

        internal void SetStatusForMessage(VATRPContact contact, string msgId, LinphoneChatMessageState status)
        {
            if (msgId.NotBlank())
            {
                if (contact != null)
                {
                    VATRPChat chat = this.FindChat(contact);
                    if ((chat != null) && msgId.NotBlank())
                    {
                        foreach (VATRPChatMessage message in chat.Messages)
                        {
                            if (message.ID == msgId)
                            {
                                if (status > message.Status)
                                {
                                    message.Status = status;
                                }
                                break;
                            }
                        }
                    }
                }
                else
                {
                    using (IEnumerator<VATRPChat> enumerator2 = this._items.GetEnumerator())
                    {
                        while (enumerator2.MoveNext())
                        {
                            foreach (VATRPChatMessage message2 in enumerator2.Current.Messages)
                            {
                                if (message2.ID == msgId)
                                {
                                    if (status > message2.Status)
                                    {
                                        message2.Status = status;
                                    }
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }

        internal void SetStatusLastMessageInActiveChat(VATRPContact contact, string msgText, LinphoneChatMessageState status)
        {
            if ((msgText != null) && (contact != null))
            {
                VATRPChat chat = this.FindChat(contact);
                if ((((chat != null) && ((chat.Messages != null) && (chat.Messages.Count != 0))) &&
                     msgText.Equals(chat.Messages[chat.Messages.Count - 1])) &&
                    (status > chat.Messages[chat.Messages.Count - 1].Status))
                {
                    chat.Messages[chat.Messages.Count - 1].Status = status;
                }
            }
        }

        private void UpdateContactStatusInChats(VATRPContact contact)
        {
            foreach (VATRPChat chat in this._items)
            {
                for (int i = 0; i < chat.Contacts.Count; i++)
                {
                    if (contact.Equals(chat.Contacts[i]))
                    {
                        chat.Contacts[i].Status = contact.Status;
                    }
                }
            }

        }

        public int ChatsCount
        {
            get
            {
                if (this._items == null)
                {
                    return 0;
                }
                return this._items.Count;
            }
        }


        public bool IsLoaded { get; set; }


        public ObservableCollection<VATRPContact> Contacts
        {
            get
            {
                if (_contacts == null)
                    _contacts = new ObservableCollection<VATRPContact>();
                return _contacts;
            }
        }

        public ObservableCollection<VATRPChat> ChatItems
        {
            get
            {
                if (_items == null)
                    _items = new ObservableCollection<VATRPChat>();
                return _items;
            }
            set { _items = value; }
        }

        internal void RemoveAllContact()
        {
            Contacts.Clear();
        }

        public bool Start()
        {
            return true;
        }

        public bool Stop()
        {
            return true;
        }
    }
}
