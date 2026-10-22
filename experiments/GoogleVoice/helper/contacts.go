package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode"

	"go.mau.fi/mautrix-gvoice/pkg/libgv"
	"go.mau.fi/mautrix-gvoice/pkg/libgv/gvproto"
)

// Contacts are read again after this long, so a name added in Google Contacts reaches the app without a restart.
const contactsRefresh = 6 * time.Hour

// Numbers looked up in one request.
const lookupBatch = 50

// Names from the account's Google contacts, by E.164 number, as upstream's bridge reads them: the whole list once,
// then each number that is not in it. A number with no contact is remembered as such, so it is not asked again.
// Names and numbers are shown in the app's own window and never written to a log.
type contactBook struct {
	mu       sync.Mutex
	contacts map[string]contactInfo
	// Avatar URLs remain in the helper. The app receives only a SHA-256 handle, which it can safely keep in its
	// encrypted media cache and ask back for after the conversation list has repopulated this book.
	avatarURLs map[string]string
	loadedAt time.Time
	// Injectable for tests.
	autocomplete func(ctx context.Context, client *libgv.Client) (*gvproto.RespAutocompleteContacts, error)
	lookup       func(ctx context.Context, client *libgv.Client, phones []string) (map[string]*gvproto.RespLookupContacts_Match, error)
}

type contactInfo struct {
	Name   string
	Avatar string
}

// The contact names for these numbers that are known. A failed request leaves the numbers unnamed rather than
// failing the conversation list, and is tried again on a later list.
func (book *contactBook) infoFor(ctx context.Context, client *libgv.Client, numbers []string) (map[string]contactInfo, map[string]any) {
	book.mu.Lock()
	defer book.mu.Unlock()
	// Counts for the diagnostic log, only when a request was made: never names or numbers.
	var stats map[string]any
	if book.contacts == nil || time.Since(book.loadedAt) > contactsRefresh {
		autocomplete := book.autocomplete
		if autocomplete == nil {
			autocomplete = func(ctx context.Context, client *libgv.Client) (*gvproto.RespAutocompleteContacts, error) {
				return client.AutocompleteContacts(ctx, "")
			}
		}
		stats = map[string]any{}
		if resp, err := autocomplete(ctx, client); err == nil {
			contacts := map[string]contactInfo{}
			for _, result := range resp.GetResults() {
				info := book.process(result.GetPerson())
				for _, method := range result.GetPerson().GetContactMethods() {
					if number := method.GetPhone().GetCanonicalValue(); number != "" && info.Name != "" {
						if _, known := contacts[number]; !known || method.GetDisplayInfo().GetPrimary() {
							contacts[number] = info
						}
					}
				}
			}
			book.contacts, book.loadedAt = contacts, time.Now()
			stats["contactList"], stats["listedNumbers"] = len(resp.GetResults()), len(contacts)
		} else {
			stats["contactList"], stats["status"] = "failed", statusOf(err)
			if book.contacts == nil {
				book.contacts = map[string]contactInfo{}
			}
		}
	}
	var missing []string
	for _, number := range numbers {
		if _, known := book.contacts[number]; !known && number != "" && len(missing) < lookupBatch {
			missing = append(missing, number)
		}
	}
	if len(missing) > 0 {
		lookup := book.lookup
		if lookup == nil {
			lookup = func(ctx context.Context, client *libgv.Client, phones []string) (map[string]*gvproto.RespLookupContacts_Match, error) {
				return client.LookupContact(ctx, phones...)
			}
		}
		if stats == nil {
			stats = map[string]any{}
		}
		stats["lookedUp"] = len(missing)
		if matches, err := lookup(ctx, client, missing); err == nil {
			stats["matched"] = len(matches)
			for _, number := range missing {
				match, found := matches[number]
				switch {
				case !found:
					book.contacts[number] = contactInfo{}
				case match.GetFailureType() == gvproto.RespLookupContacts_Match_NO_FAILURE:
					book.contacts[number] = book.process(match.GetAutocompletion().GetPerson())
				case match.GetFailureType() == gvproto.RespLookupContacts_Match_PERMANENT:
					book.contacts[number] = contactInfo{}
				}
			}
		} else {
			stats["lookup"], stats["status"] = "failed", statusOf(err)
		}
	}
	named := map[string]contactInfo{}
	for _, number := range numbers {
		if info := book.contacts[number]; info.Name != "" {
			named[number] = info
		}
	}
	if stats != nil {
		stats["unnamed"], stats["named"] = len(numbers), len(named)
	}
	return named, stats
}

// process returns what this app needs from a Google Contact. A contact-provided photo is represented by an opaque
// stable handle, not its URL. Google Voice uses MONOGRAM when there is no person photo; preserve the native initials
// avatar in that case.
func (book *contactBook) process(person *gvproto.Person) contactInfo {
	info := contactInfo{Name: contactName(person)}
	for _, method := range person.GetContactMethods() {
		photo := method.GetDisplayInfo().GetPhoto()
		if photo.GetType() == gvproto.ContactDisplayInfo_Photo_MONOGRAM || photo.GetURL() == "" || (info.Avatar != "" && !method.GetDisplayInfo().GetPrimary()) {
			continue
		}
		if book.avatarURLs == nil {
			book.avatarURLs = map[string]string{}
		}
		sum := sha256.Sum256([]byte(photo.GetURL()))
		info.Avatar = "avatar:" + hex.EncodeToString(sum[:])
		book.avatarURLs[info.Avatar] = photo.GetURL()
	}
	return info
}

// avatarURL resolves a handle the helper itself issued. It never exposes the original URL across the pipe.
func (book *contactBook) avatarURL(id string) string {
	book.mu.Lock()
	defer book.mu.Unlock()
	return book.avatarURLs[id]
}

// A person's display name: the primary contact method's name, or the first one given.
func contactName(person *gvproto.Person) string {
	name := ""
	for _, method := range person.GetContactMethods() {
		if value := method.GetDisplayInfo().GetName().GetValue(); value != "" && (name == "" || method.GetDisplayInfo().GetPrimary()) {
			name = value
		}
	}
	return name
}

// The numbers in these threads that the threads themselves do not name.
func unnamedNumbers(threads []*gvproto.Thread) []string {
	seen := map[string]bool{}
	var numbers []string
	add := func(number string) {
		if number != "" && !seen[number] {
			seen[number] = true
			numbers = append(numbers, number)
		}
	}
	for _, thread := range threads {
		named := map[string]bool{}
		for _, contact := range thread.GetContacts() {
			if !numberOnly(contact.GetName()) {
				named[contact.GetPhoneNumber()] = true
			} else {
				add(contact.GetPhoneNumber())
			}
		}
		for _, number := range thread.GetPhoneNumbers() {
			if !named[number] {
				add(number)
			}
		}
	}
	return numbers
}

// Every participant is eligible for a contact photo, even when Voice supplied a display name on the thread itself.
// `convertThreadNamed` still gives that thread name precedence; contact data only fills a number-only name.
func participantNumbers(threads []*gvproto.Thread) []string {
	seen := map[string]bool{}
	numbers := make([]string, 0)
	add := func(number string) {
		if number != "" && !seen[number] {
			seen[number] = true
			numbers = append(numbers, number)
		}
	}
	for _, thread := range threads {
		for _, contact := range thread.GetContacts() {
			add(contact.GetPhoneNumber())
		}
		for _, number := range thread.GetPhoneNumbers() {
			add(number)
		}
	}
	return numbers
}

// Voice gives a caller with no contact their number as a name. A name without a single letter is only a number.
func numberOnly(name string) bool {
	return !strings.ContainsFunc(name, unicode.IsLetter)
}

type voiceContact struct {
	Number string `json:"number"`
	Name   string `json:"name"`
	Avatar string `json:"avatar,omitempty"`
}

// The account's named contacts, for New chat to search. Read from the same book the conversation list uses.
func (book *contactBook) all(ctx context.Context, client *libgv.Client) []voiceContact {
	_, _ = book.infoFor(ctx, client, nil)
	book.mu.Lock()
	defer book.mu.Unlock()
	contacts := make([]voiceContact, 0, len(book.contacts))
	for number, info := range book.contacts {
		if info.Name != "" {
			contacts = append(contacts, voiceContact{Number: number, Name: info.Name, Avatar: info.Avatar})
		}
	}
	slices.SortFunc(contacts, func(a, b voiceContact) int { return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)) })
	return contacts
}
