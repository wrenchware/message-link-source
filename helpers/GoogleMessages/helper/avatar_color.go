package main

import (
	"strings"

	"go.mau.fi/mautrix-gmessages/pkg/libgm/gmproto"
)

// Google's color for the other person in a direct chat, or the conversation's own color,
// used to tint letter avatars. Only #RRGGBB or #AARRGGBB hex values are passed on.
func avatarColor(c *gmproto.Conversation) string {
	color := c.GetAvatarHexColor()
	if !c.GetIsGroupChat() {
		for _, p := range c.GetParticipants() {
			if !p.GetIsMe() && p.GetAvatarHexColor() != "" {
				color = p.GetAvatarHexColor()
				break
			}
		}
	}
	return validHexColor(color)
}

func validHexColor(value string) string {
	value = strings.TrimPrefix(strings.TrimSpace(value), "#")
	if len(value) != 6 && len(value) != 8 {
		return ""
	}
	for _, r := range value {
		if !(r >= '0' && r <= '9' || r >= 'a' && r <= 'f' || r >= 'A' && r <= 'F') {
			return ""
		}
	}
	return "#" + value
}
