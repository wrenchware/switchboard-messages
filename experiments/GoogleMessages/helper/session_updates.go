package main

import (
	"encoding/json"

	"go.mau.fi/mautrix-gmessages/pkg/libgm"
)

// Private inherited pipe only. Never log this payload or send it to the UI feed.
func (a *adapter) publishSession() {
	if a.client == nil {
		return
	}
	if data, err := sessionJSON(a.client.AuthData); err == nil {
		a.out.write(response{Event: "session", Result: data})
	}
}

// The session as the app saves it, encoded under the cookie lock. The library writes the cookie map from every answer it
// reads, and snapshot, pair.finish and reauthenticate used to hand back the live session, which was encoded later with no
// lock: meeting a write there stops the helper with a concurrent map access. The JSON is the same either way.
func sessionJSON(auth *libgm.AuthData) (json.RawMessage, error) {
	auth.CookiesLock.RLock()
	defer auth.CookiesLock.RUnlock()
	return json.Marshal(auth)
}

// A request's answer that is the session.
func sessionAnswer(auth *libgm.AuthData) (any, string) {
	data, err := sessionJSON(auth)
	if err != nil {
		return nil, "invalid_session"
	}
	return data, ""
}
