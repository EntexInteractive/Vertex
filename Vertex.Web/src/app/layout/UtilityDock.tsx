import {useState} from 'react'
import {Avatar} from '../../components/Avatar'
import {Icon} from '../../components/Icon'
import {messages, storage} from '../../mocks/sampleData'

/** The dock in the bottom-right corner with team messages and server storage. Both are previews with sample data. */
export function UtilityDock() {
    const [panel, setPanel] = useState<'chat' | 'storage' | null>(null)
    const usedPercent = Math.round((storage.usedGb / storage.totalGb) * 100)

    return (
        <div className="utilities">
            {panel === 'chat' && (
                <div className="utility-popover">
                    <div className="utility-title">
                        <span>
                            <Icon name="message" size={15}/>
                            Team messages
                            <em className="preview-tag">Preview</em>
                        </span>
                        <button type="button" aria-label="Close" onClick={() => setPanel(null)}>×</button>
                    </div>
                    {messages.map(message => (
                        <div className="message" key={message.text}>
                            <Avatar person={message.author}/>
                            <div>
                                <b>
                                    {message.author.name} <small>{message.when}</small>
                                </b>
                                <p>{message.text}</p>
                            </div>
                        </div>
                    ))}
                    <button type="button" className="utility-action" disabled>Open team chat</button>
                </div>
            )}
            {panel === 'storage' && (
                <div className="utility-popover">
                    <div className="utility-title">
                        <span>
                            <Icon name="storage" size={15}/>
                            Server storage
                            <em className="preview-tag">Preview</em>
                        </span>
                        <button type="button" aria-label="Close" onClick={() => setPanel(null)}>×</button>
                    </div>
                    <div className="storage-number">
                        <strong>{storage.usedGb} GB</strong>
                        <small>of {storage.totalGb} GB</small>
                    </div>
                    <div className="storage-bar">
                        <i style={{width: `${usedPercent}%`}}/>
                    </div>
                    <div className="storage-item">
                        <span><i/>File content</span>
                        <b>{storage.contentGb} GB</b>
                    </div>
                    <div className="storage-item">
                        <span><i className="artifact"/>Build artifacts</span>
                        <b>{storage.artifactsGb} GB</b>
                    </div>
                    <button type="button" className="utility-action" disabled>Manage server storage</button>
                </div>
            )}
            <div className="utility-dock">
                <button
                    type="button"
                    className={panel === 'chat' ? 'active' : ''}
                    onClick={() => setPanel(panel === 'chat' ? null : 'chat')}
                    aria-label="Toggle team messages"
                    title="Messages"
                >
                    <Icon name="message" size={15}/>
                    Messages <b>{messages.length}</b>
                </button>
                <button
                    type="button"
                    className={panel === 'storage' ? 'active' : ''}
                    onClick={() => setPanel(panel === 'storage' ? null : 'storage')}
                    aria-label="Toggle server storage"
                    title="Storage"
                >
                    <Icon name="storage" size={15}/>
                    Storage <small>{usedPercent}%</small>
                </button>
            </div>
        </div>
    )
}
