package main

import (
	"strconv"
	"time"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	waTypes "go.mau.fi/whatsmeow/types"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// MarkRead reports a socket write, not a server acknowledgement. The pinned
// library otherwise discards receipt acks. Observe its typed nodes before they
// are formatted: never enable a raw protocol dump or print node content.
type receiptLog struct {
	waLog.Logger
	module string
}

func (l *receiptLog) Sub(module string) waLog.Logger {
	return &receiptLog{Logger: l.Logger.Sub(module), module: module}
}

func (l *receiptLog) Debugf(format string, args ...any) {
	if format != "%s" || len(args) != 1 {
		return
	}
	node, ok := args[0].(*waBinary.Node)
	if !ok || node == nil {
		return
	}
	id, _ := node.Attrs["id"].(string)
	if id == "" {
		return
	}
	kind, _ := node.Attrs["type"].(string)
	if l.module == "Send" && node.Tag == "receipt" && (kind == "read" || kind == "read-self") {
		to, _ := node.Attrs["to"].(waTypes.JID)
		address := "other"
		switch to.Server {
		case waTypes.MessengerServer:
			address = "direct"
		case waTypes.GroupServer:
			address = "group"
		}
		_, participant := node.Attrs["participant"]
		list := node.GetChildByTag("list")
		listed := len(list.GetChildren())
		stamp, validStamp := receiptInteger(node.Attrs["t"])
		var age int64
		if validStamp {
			age = time.Now().Unix() - stamp
		}
		logf("e2ee receipt frame id=%s mode=%s address=%s device=%v participant=%v listed=%d timestamp=%v ageSeconds=%d",
			tagOf(id), kind, address, to.Device != 0, participant, listed, validStamp, age)
	} else if l.module == "Recv" && node.Tag == "receipt" && (kind == "read" || kind == "read-self") {
		from, _ := node.Attrs["from"].(waTypes.JID)
		recipient, hasRecipient := node.Attrs["recipient"].(waTypes.JID)
		_, hasParticipant := node.Attrs["participant"]
		stamp, valid := receiptInteger(node.Attrs["t"])
		var secondsAge, millisAge int64
		if valid {
			secondsAge = time.Now().Unix() - stamp
			millisAge = time.Now().Unix() - stamp/1000
		}
		logf("e2ee receipt incoming id=%s mode=%s from=%s fromDevice=%v recipient=%v recipientTag=%s participant=%v timestamp=%v secondsAge=%d millisAge=%d children=%d",
			tagOf(id), kind, tagOf(from.User), from.Device != 0, hasRecipient, tagOf(recipient.User), hasParticipant, valid, secondsAge, millisAge, len(node.GetChildren()))
	} else if l.module == "Recv" && node.Tag == "ack" {
		class, _ := node.Attrs["class"].(string)
		if class != "receipt" {
			return
		}
		// Only fixed labels and small numeric protocol error codes reach the log.
		mode := "other"
		switch kind {
		case "", "read", "read-self", "sender", "inactive", "retry":
			mode = kind
		}
		code, validCode := receiptInteger(node.Attrs["error"])
		_, errorAttribute := node.Attrs["error"]
		_, errorChild := node.GetOptionalChildByTag("error")
		if !validCode || code < 0 || code > 9999 {
			code = -1
		}
		logf("e2ee receipt ack id=%s mode=%q errorAttribute=%v errorChild=%v code=%d",
			tagOf(id), mode, errorAttribute, errorChild, code)
	}
}

func receiptInteger(value any) (int64, bool) {
	switch value := value.(type) {
	case int:
		return int64(value), true
	case int64:
		return value, true
	case string:
		number, err := strconv.ParseInt(value, 10, 64)
		return number, err == nil
	default:
		return 0, false
	}
}

// PrepareE2EEClient does not accept a logger, and changing Client.Log afterwards
// leaves its private Send/Recv loggers unchanged. Construct the still-disconnected
// client with our observer and retain all settings assigned by the pinned
// messagix PrepareE2EEClient. No session/device is created or registered here.
func observeReceiptAcks(prepared *whatsmeow.Client) *whatsmeow.Client {
	client := whatsmeow.NewClient(prepared.Store, &receiptLog{Logger: prepared.Log})
	client.GetClientPayload = prepared.GetClientPayload
	client.MessengerConfig = prepared.MessengerConfig
	client.RefreshCAT = prepared.RefreshCAT
	client.EnableAutoReconnect = prepared.EnableAutoReconnect
	client.InitialAutoReconnect = prepared.InitialAutoReconnect
	return client
}
