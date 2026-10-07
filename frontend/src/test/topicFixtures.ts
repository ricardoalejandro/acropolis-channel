import type { AdminTopic, ContentTopics, TopicDirectory } from '../api/topics';
export const topicId = '40000000-0000-0000-0000-000000000001';
export const contentId = '40000000-0000-0000-0000-000000000002';
export const otherTopicId = '40000000-0000-0000-0000-000000000003';
export const topicVersion = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
export const nextTopicVersion = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
export const topic: AdminTopic = {
  id: topicId,
  slug: 'tema-qa',
  name: 'Tema QA',
  status: 'active',
  position: 0,
  version: topicVersion,
};
export const otherTopic: AdminTopic = {
  ...topic,
  id: otherTopicId,
  slug: 'otro-tema-qa',
  name: 'Otro tema QA',
  position: 1,
};
export const topicDirectory: TopicDirectory = {
  items: [topic, otherTopic],
  total: 2,
  page: 1,
  pageSize: 20,
  directoryVersion: topicVersion,
};
export const contentTopics: ContentTopics = {
  contentId,
  contentVersion: topicVersion,
  items: [topic],
};
