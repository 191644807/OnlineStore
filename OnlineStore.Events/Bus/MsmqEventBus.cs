﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Messaging;
using System.Reflection;
using OnlineStore.Infrastructure;

namespace OnlineStore.Events.Bus
{
    // 基于微软MSMQ的EventBus的实现
    public class MsmqEventBus : DisposableObject, IEventBus
    {
        #region Private Fields

        private readonly Guid _id = Guid.NewGuid();
        private volatile bool _committed = true;
        private readonly bool _useInternalTransaction;
        private readonly MessageQueue _messageQueue = new MessageQueue(@".""Private$""MsMqTest");
        private readonly IEventAggregator _aggregator;
        private readonly MethodInfo _publishMethod;

        #endregion

        #region Ctor

        public MsmqEventBus(string path)
        {
            this._aggregator = ServiceLocator.Instance.GetService<IEventAggregator>();

            _publishMethod = (from m in _aggregator.GetType().GetMethods()
                let parameters = m.GetParameters()
                let methodName = m.Name
                where methodName == "Handle" &&
                      parameters != null &&
                      parameters.Length == 1
                select m).First();
            var options = new MsmqBusOptions(path);

            // 初始化消息队列对象
            // 更多信息参考：https://msdn.microsoft.com/zh-cn/library/System.Messaging.MessageQueue(v=vs.110).aspx 
            this._messageQueue = new MessageQueue(path,
                options.SharedModeDenyReceive,
                options.EnableCache, options.QueueAccessMode);

            this._useInternalTransaction = options.UseInternalTransaction && _messageQueue.Transactional;
        }

        #endregion

        #region IEventBus Members

        public Guid Id
        {
            get { return _id; }
        }

        public void Publish<TMessage>(TMessage message) where TMessage : class, IEvent
        {
            // 将消息放入Message中Body属性进行序列化发送到消息队列中
            var msmqMessage = new Message(message) { Formatter = new XmlMessageFormatter(new[] { message.GetType() }), Label = message.GetType().ToString()};
            _messageQueue.Send(msmqMessage);
            _committed = false;
        }

        public void Publish<TMessage>(IEnumerable<TMessage> messages) where TMessage : class, IEvent
        {
            messages.ToList().ForEach(m =>
            {
                _messageQueue.Send(m);
                _committed = false;
            });
        }

        public void Clear()
        {
            this._messageQueue.Close();
        }

        public void Commit()
        {
            if (this._useInternalTransaction)
            {
                using (var transaction = new MessageQueueTransaction())
                {
                    try
                    {
                        transaction.Begin();
                        var message = _messageQueue.Receive();
                        if (message != null)
                        {
                            message.Formatter = new XmlMessageFormatter(new[] { typeof(string) });
                            var evntType = ConvertStringToType(message.Body.ToString());
                            var method = _publishMethod.MakeGenericMethod(evntType);
                            var evnt = Activator.CreateInstance(evntType);
                            method.Invoke(_aggregator, new object[] { evnt });

                            transaction.Commit();
                        }
                    }
                    catch
                    {
                        transaction.Abort();
                        throw;
                    }
                }
            }
            else
            {
                // 从msmq消息队列中出队，此时获得的对象是消息对象
                var message = _messageQueue.Receive();
                if (message != null)
                {
                    // 指定反序列化的对象，由于我们之前把对应的事件类型保存在MessageQueue中的Label属性
                    // 所以此时可以通过Label属性来获得目标序列化类型
                    message.Formatter = new XmlMessageFormatter(new[] { ConvertStringToType(message.Label) });
                    
                    // 这样message.Body获得就是对应的事件对象，后面的处理逻辑就和EventBus一样了
                    var evntType =message.Body.GetType();
                    var method = _publishMethod.MakeGenericMethod(evntType);
                    method.Invoke(_aggregator, new object[] { message.Body });
                }
            }

            _committed = true;
        }

        #endregion

        #region DisposableObject Members

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            if (_messageQueue == null) return;

            _messageQueue.Close();
            _messageQueue.Dispose();
        }

        #endregion

        private Type ConvertStringToType(string sourceStr)
        {
            return Type.GetType(sourceStr + ", OnlineStore.Domain");
        }
    }
}
